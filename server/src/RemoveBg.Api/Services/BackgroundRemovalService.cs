using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using RemoveBg.Api.Configuration;
using RemoveBg.Api.Infrastructure;
using SkiaSharp;

namespace RemoveBg.Api.Services;

public interface IBackgroundRemovalService
{
    Task<RemovalResult> RemoveAsync(byte[] input, CancellationToken cancellationToken);
}

public sealed record RemovalResult(byte[] Png, int Width, int Height, long DurationMs);

public sealed class BackgroundRemovalService : IBackgroundRemovalService, IDisposable
{
    private static readonly float[] Mean = [0.485f, 0.456f, 0.406f];
    private static readonly float[] Std = [0.229f, 0.224f, 0.225f];

    private readonly RemovalOptions options;
    private readonly IHttpClientFactory httpClientFactory;
    private readonly ILogger<BackgroundRemovalService> logger;
    private readonly SemaphoreSlim workers;
    private readonly SemaphoreSlim initializationLock = new(1, 1);
    private InferenceSession? session;

    public BackgroundRemovalService(
        IOptions<RemovalOptions> options,
        IHttpClientFactory httpClientFactory,
        ILogger<BackgroundRemovalService> logger)
    {
        this.options = options.Value;
        this.httpClientFactory = httpClientFactory;
        this.logger = logger;
        workers = new SemaphoreSlim(this.options.CpuWorkers, this.options.CpuWorkers);
    }

    public async Task<RemovalResult> RemoveAsync(byte[] input, CancellationToken cancellationToken)
    {
        ValidateImage(input);
        await workers.WaitAsync(cancellationToken);
        try
        {
            var activeSession = await GetSessionAsync(cancellationToken);
            return await Task.Run(() => RunInference(input, activeSession), cancellationToken);
        }
        finally
        {
            workers.Release();
        }
    }

    private void ValidateImage(byte[] input)
    {
        using var stream = new SKMemoryStream(input);
        using var codec = SKCodec.Create(stream);
        if (codec is null)
        {
            throw new ApiException(422, "invalid_image", "Файл не является корректным изображением");
        }
        if ((long)codec.Info.Width * codec.Info.Height > options.MaxImagePixels)
        {
            throw new ApiException(
                413,
                "image_too_large",
                $"Допустимо не более {options.MaxImagePixels} пикселей");
        }
    }

    private async Task<InferenceSession> GetSessionAsync(CancellationToken cancellationToken)
    {
        if (session is not null)
        {
            return session;
        }

        await initializationLock.WaitAsync(cancellationToken);
        try
        {
            if (session is not null)
            {
                return session;
            }

            await EnsureModelAsync(cancellationToken);
            var sessionOptions = new Microsoft.ML.OnnxRuntime.SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                IntraOpNumThreads = options.IntraOpThreads,
                InterOpNumThreads = 1,
                ExecutionMode = ExecutionMode.ORT_SEQUENTIAL
            };
            session = new InferenceSession(options.ModelPath, sessionOptions);
            logger.LogInformation("Loaded ONNX model {ModelPath} with CPU execution provider", options.ModelPath);
            return session;
        }
        finally
        {
            initializationLock.Release();
        }
    }

    private async Task EnsureModelAsync(CancellationToken cancellationToken)
    {
        if (File.Exists(options.ModelPath))
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(options.ModelUrl))
        {
            throw new ApiException(503, "model_missing", "ONNX-модель не найдена");
        }

        var directory = Path.GetDirectoryName(options.ModelPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = $"{options.ModelPath}.{Guid.NewGuid():N}.download";
        try
        {
            logger.LogInformation("Downloading ONNX model from {ModelUrl}", options.ModelUrl);
            using var response = await httpClientFactory.CreateClient("model-download")
                .GetAsync(options.ModelUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using (var target = File.Create(temporaryPath))
            {
                await response.Content.CopyToAsync(target, cancellationToken);
            }

            if (!string.IsNullOrWhiteSpace(options.ModelMd5))
            {
                await using var modelStream = File.OpenRead(temporaryPath);
                var actual = Convert.ToHexString(await MD5.HashDataAsync(modelStream, cancellationToken))
                    .ToLowerInvariant();
                if (!string.Equals(actual, options.ModelMd5, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ApiException(503, "model_checksum_failed", "Контрольная сумма модели не совпала");
                }
            }

            File.Move(temporaryPath, options.ModelPath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private RemovalResult RunInference(byte[] input, InferenceSession activeSession)
    {
        var stopwatch = Stopwatch.StartNew();
        using var original = SKBitmap.Decode(input)
            ?? throw new ApiException(422, "invalid_image", "Не удалось декодировать изображение");
        using var resized = new SKBitmap(new SKImageInfo(
            options.InputSize,
            options.InputSize,
            SKColorType.Rgba8888,
            SKAlphaType.Unpremul));
        using (var canvas = new SKCanvas(resized))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.DrawBitmap(
                original,
                new SKRect(0, 0, options.InputSize, options.InputSize),
                new SKSamplingOptions(SKCubicResampler.Mitchell));
        }

        var pixels = options.InputSize * options.InputSize;
        var tensor = new float[pixels * 3];
        for (var y = 0; y < options.InputSize; y++)
        {
            for (var x = 0; x < options.InputSize; x++)
            {
                var color = resized.GetPixel(x, y);
                var index = y * options.InputSize + x;
                tensor[index] = (color.Red / 255f - Mean[0]) / Std[0];
                tensor[pixels + index] = (color.Green / 255f - Mean[1]) / Std[1];
                tensor[2 * pixels + index] = (color.Blue / 255f - Mean[2]) / Std[2];
            }
        }

        using var inputValue = OrtValue.CreateTensorValueFromMemory(
            tensor,
            [1, 3, options.InputSize, options.InputSize]);
        using var runOptions = new RunOptions();
        var inputNames = new[] { activeSession.InputNames[0] };
        var inputValues = new[] { inputValue };
        using var outputs = activeSession.Run(
            runOptions,
            inputNames,
            inputValues,
            activeSession.OutputNames);
        var rawMask = outputs[0].GetTensorDataAsSpan<float>();
        if (rawMask.Length < pixels)
        {
            throw new ApiException(503, "invalid_model_output", "Модель вернула некорректную маску");
        }

        var normalized = new float[pixels];
        var min = float.MaxValue;
        var max = float.MinValue;
        for (var i = 0; i < pixels; i++)
        {
            var value = Sigmoid(rawMask[i]);
            normalized[i] = value;
            min = Math.Min(min, value);
            max = Math.Max(max, value);
        }

        var range = Math.Max(max - min, float.Epsilon);
        var mask = new byte[pixels];
        for (var i = 0; i < pixels; i++)
        {
            mask[i] = (byte)Math.Clamp((normalized[i] - min) / range * 255f, 0, 255);
        }

        using var outputBitmap = new SKBitmap(new SKImageInfo(
            original.Width,
            original.Height,
            SKColorType.Rgba8888,
            SKAlphaType.Unpremul));
        for (var y = 0; y < original.Height; y++)
        {
            for (var x = 0; x < original.Width; x++)
            {
                var color = original.GetPixel(x, y);
                var alpha = SampleMask(mask, x, y, original.Width, original.Height, options.InputSize);
                var combinedAlpha = (byte)(color.Alpha * alpha / 255);
                outputBitmap.SetPixel(x, y, new SKColor(color.Red, color.Green, color.Blue, combinedAlpha));
            }
        }

        using var outputImage = SKImage.FromBitmap(outputBitmap);
        using var outputData = outputImage.Encode(SKEncodedImageFormat.Png, 100);
        stopwatch.Stop();
        return new RemovalResult(outputData.ToArray(), original.Width, original.Height, stopwatch.ElapsedMilliseconds);
    }

    private static byte SampleMask(
        byte[] mask,
        int x,
        int y,
        int targetWidth,
        int targetHeight,
        int sourceSize)
    {
        var sourceX = (x + 0.5f) * sourceSize / targetWidth - 0.5f;
        var sourceY = (y + 0.5f) * sourceSize / targetHeight - 0.5f;
        var x0 = Math.Clamp((int)MathF.Floor(sourceX), 0, sourceSize - 1);
        var y0 = Math.Clamp((int)MathF.Floor(sourceY), 0, sourceSize - 1);
        var x1 = Math.Min(x0 + 1, sourceSize - 1);
        var y1 = Math.Min(y0 + 1, sourceSize - 1);
        var dx = Math.Clamp(sourceX - x0, 0, 1);
        var dy = Math.Clamp(sourceY - y0, 0, 1);
        var top = mask[y0 * sourceSize + x0] * (1 - dx) + mask[y0 * sourceSize + x1] * dx;
        var bottom = mask[y1 * sourceSize + x0] * (1 - dx) + mask[y1 * sourceSize + x1] * dx;
        return (byte)Math.Clamp(top * (1 - dy) + bottom * dy, 0, 255);
    }

    private static float Sigmoid(float value)
    {
        if (value >= 0)
        {
            return 1f / (1f + MathF.Exp(-value));
        }
        var exponent = MathF.Exp(value);
        return exponent / (1f + exponent);
    }

    public void Dispose()
    {
        session?.Dispose();
        workers.Dispose();
        initializationLock.Dispose();
    }
}
