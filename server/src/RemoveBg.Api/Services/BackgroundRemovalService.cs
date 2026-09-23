using System.Buffers;
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
    bool IsReady { get; }
    Task WarmUpAsync(CancellationToken cancellationToken);
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
    private volatile InferenceSession? session;

    public bool IsReady => session is not null;

    public BackgroundRemovalService(
        IOptions<RemovalOptions> options,
        IHttpClientFactory httpClientFactory,
        ILogger<BackgroundRemovalService> logger)
    {
        this.options = options.Value;
        this.httpClientFactory = httpClientFactory;
        this.logger = logger;
        var workerCount = Math.Max(1, this.options.CpuWorkers);
        workers = new SemaphoreSlim(workerCount, workerCount);
    }

    public async Task WarmUpAsync(CancellationToken cancellationToken) =>
        _ = await GetSessionAsync(cancellationToken);

    public async Task<RemovalResult> RemoveAsync(byte[] input, CancellationToken cancellationToken)
    {
        var imageInfo = ValidateImage(input);
        if (!IsReady)
        {
            throw new ApiException(
                StatusCodes.Status503ServiceUnavailable,
                "model_warming_up",
                "Модель запускается, повторите запрос через несколько секунд");
        }

        var entered = await workers.WaitAsync(
            TimeSpan.FromSeconds(Math.Max(0, options.QueueWaitSeconds)),
            cancellationToken);
        if (!entered)
        {
            throw new ApiException(
                StatusCodes.Status429TooManyRequests,
                "server_busy",
                "Очередь обработки занята, повторите запрос позже");
        }

        try
        {
            var activeSession = await GetSessionAsync(cancellationToken);
            return await Task.Run(
                () => RunInference(input, imageInfo, activeSession),
                cancellationToken);
        }
        finally
        {
            workers.Release();
        }
    }

    private SKImageInfo ValidateImage(byte[] input)
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
        return codec.Info;
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

    private RemovalResult RunInference(
        byte[] input,
        SKImageInfo sourceInfo,
        InferenceSession activeSession)
    {
        var stopwatch = Stopwatch.StartNew();
        var decodedInfo = new SKImageInfo(
            sourceInfo.Width,
            sourceInfo.Height,
            SKColorType.Rgba8888,
            SKAlphaType.Unpremul);
        using var original = SKBitmap.Decode(input, decodedInfo)
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
        var tensorLength = pixels * 3;
        var tensor = ArrayPool<float>.Shared.Rent(tensorLength);
        try
        {
            FillInputTensor(resized, tensor, pixels);
            using var inputValue = OrtValue.CreateTensorValueFromMemory<float>(
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
            var rawMask = outputs[0].GetTensorMutableDataAsSpan<float>();
            if (rawMask.Length < pixels)
            {
                throw new ApiException(503, "invalid_model_output", "Модель вернула некорректную маску");
            }

            NormalizeMaskInPlace(rawMask[..pixels]);
            ApplyMask(original, rawMask[..pixels], options.InputSize);
        }
        finally
        {
            ArrayPool<float>.Shared.Return(tensor);
        }

        using var output = new MemoryStream();
        if (!original.Encode(output, SKEncodedImageFormat.Png, 100))
        {
            throw new ApiException(503, "encoding_failed", "Не удалось закодировать результат");
        }
        stopwatch.Stop();
        return new RemovalResult(
            output.ToArray(),
            original.Width,
            original.Height,
            stopwatch.ElapsedMilliseconds);
    }

    private void FillInputTensor(SKBitmap bitmap, float[] tensor, int pixels)
    {
        var source = bitmap.GetPixelSpan();
        for (var y = 0; y < bitmap.Height; y++)
        {
            var row = y * bitmap.RowBytes;
            var pixelRow = y * bitmap.Width;
            for (var x = 0; x < bitmap.Width; x++)
            {
                var sourceIndex = row + x * 4;
                var targetIndex = pixelRow + x;
                tensor[targetIndex] = (source[sourceIndex] / 255f - Mean[0]) / Std[0];
                tensor[pixels + targetIndex] = (source[sourceIndex + 1] / 255f - Mean[1]) / Std[1];
                tensor[2 * pixels + targetIndex] = (source[sourceIndex + 2] / 255f - Mean[2]) / Std[2];
            }
        }
    }

    internal static void NormalizeMaskInPlace(Span<float> mask)
    {
        var min = float.MaxValue;
        var max = float.MinValue;
        for (var i = 0; i < mask.Length; i++)
        {
            var value = Sigmoid(mask[i]);
            mask[i] = value;
            min = Math.Min(min, value);
            max = Math.Max(max, value);
        }

        var range = Math.Max(max - min, float.Epsilon);
        for (var i = 0; i < mask.Length; i++)
        {
            mask[i] = Math.Clamp((mask[i] - min) / range, 0f, 1f);
        }
    }

    internal static void ApplyMask(SKBitmap original, ReadOnlySpan<float> mask, int sourceSize)
    {
        using var maskBitmap = new SKBitmap(new SKImageInfo(
            sourceSize,
            sourceSize,
            SKColorType.Alpha8,
            SKAlphaType.Premul));
        var maskPixels = maskBitmap.GetPixelSpan();
        for (var y = 0; y < sourceSize; y++)
        {
            var sourceRow = y * sourceSize;
            var targetRow = y * maskBitmap.RowBytes;
            for (var x = 0; x < sourceSize; x++)
            {
                maskPixels[targetRow + x] = (byte)MathF.Round(mask[sourceRow + x] * 255f);
            }
        }

        using var paint = new SKPaint { BlendMode = SKBlendMode.DstIn };
        using var canvas = new SKCanvas(original);
        canvas.DrawBitmap(
            maskBitmap,
            new SKRect(0, 0, original.Width, original.Height),
            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None),
            paint);
        canvas.Flush();
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

public sealed class BackgroundRemovalWarmupService(
    IBackgroundRemovalService removal,
    ILogger<BackgroundRemovalWarmupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested && !removal.IsReady)
        {
            try
            {
                await removal.WarmUpAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Failed to warm up background removal model; retrying");
                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
            }
        }
    }
}
