using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using RemoveBg.Api.Configuration;
using RemoveBg.Api.Infrastructure;
using SkiaSharp;

namespace RemoveBg.Api.Services;

public interface IBackgroundRemovalService
{
    bool IsReady { get; }
    string Device { get; }
    Task WarmUpAsync(CancellationToken cancellationToken);
    Task<RemovalResult> RemoveAsync(byte[] input, CancellationToken cancellationToken);
}

public sealed record RemovalResult(byte[] Png, int Width, int Height, long DurationMs);

public sealed class BackgroundRemovalService : IBackgroundRemovalService, IDisposable
{
    private static readonly float[] Mean = [0.485f, 0.456f, 0.406f];
    private static readonly float[] Std = [0.229f, 0.224f, 0.225f];

    private readonly RemovalOptions options;
    private readonly RemovalRuntimeSettings runtime;
    private readonly IHttpClientFactory httpClientFactory;
    private readonly ILogger<BackgroundRemovalService> logger;
    private readonly PrePackedWeightsContainer prepackedWeights = new();
    private readonly SemaphoreSlim initializationLock = new(1, 1);
    private volatile Channel<InferenceSession>? sessionPool;
    private InferenceSession[] sessions = [];
    private string[] inputNames = [];
    private string[] outputNames = [];
    private string device = "initializing";

    public bool IsReady => sessionPool is not null;
    public string Device => device;

    public BackgroundRemovalService(
        IOptions<RemovalOptions> options,
        RemovalRuntimeSettings runtime,
        IHttpClientFactory httpClientFactory,
        ILogger<BackgroundRemovalService> logger)
    {
        this.options = options.Value;
        this.runtime = runtime;
        this.httpClientFactory = httpClientFactory;
        this.logger = logger;
    }

    public async Task WarmUpAsync(CancellationToken cancellationToken) =>
        _ = await GetSessionPoolAsync(cancellationToken);

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

        var pool = await GetSessionPoolAsync(cancellationToken);
        var activeSession = await pool.Reader.ReadAsync(cancellationToken);
        try
        {
            return await Task.Run(
                () => RunInference(input, imageInfo, activeSession),
                cancellationToken);
        }
        finally
        {
            if (!pool.Writer.TryWrite(activeSession))
            {
                logger.LogError("Failed to return an ONNX session to the worker pool");
            }
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

    private async Task<Channel<InferenceSession>> GetSessionPoolAsync(
        CancellationToken cancellationToken)
    {
        if (sessionPool is not null)
        {
            return sessionPool;
        }

        await initializationLock.WaitAsync(cancellationToken);
        try
        {
            if (sessionPool is not null)
            {
                return sessionPool;
            }

            await EnsureModelAsync(cancellationToken);
            var createdSessions = new List<InferenceSession>(runtime.WorkerCount);
            try
            {
                for (var worker = 0; worker < runtime.WorkerCount; worker++)
                {
                    using var sessionOptions = CreateSessionOptions();
                    createdSessions.Add(new InferenceSession(
                        options.ModelPath,
                        sessionOptions,
                        prepackedWeights));
                }
            }
            catch
            {
                foreach (var createdSession in createdSessions)
                {
                    createdSession.Dispose();
                }
                throw;
            }

            sessions = createdSessions.ToArray();
            inputNames = [sessions[0].InputNames[0]];
            outputNames = sessions[0].OutputNames.ToArray();
            var pool = Channel.CreateBounded<InferenceSession>(new BoundedChannelOptions(
                runtime.WorkerCount)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = false,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });
            foreach (var createdSession in sessions)
            {
                pool.Writer.TryWrite(createdSession);
            }
            sessionPool = pool;
            logger.LogInformation(
                "Loaded {SessionCount} ONNX sessions for {ModelPath} with {ExecutionProvider}; intra-op threads per session={IntraOpThreads}",
                sessions.Length,
                options.ModelPath,
                device,
                runtime.IntraOpThreads);
            return pool;
        }
        finally
        {
            initializationLock.Release();
        }
    }

    private Microsoft.ML.OnnxRuntime.SessionOptions CreateSessionOptions()
    {
        var sessionOptions = new Microsoft.ML.OnnxRuntime.SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            IntraOpNumThreads = runtime.IntraOpThreads,
            InterOpNumThreads = 1,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL
        };

        var provider = options.ExecutionProvider.Trim();
        var useCoreMl = provider.Equals("CoreML", StringComparison.OrdinalIgnoreCase)
            || provider.Equals("Auto", StringComparison.OrdinalIgnoreCase)
                && OperatingSystem.IsMacOS()
                && RuntimeInformation.ProcessArchitecture == Architecture.Arm64;

        if (!useCoreMl)
        {
            device = "cpu";
            return sessionOptions;
        }

        try
        {
            sessionOptions.AppendExecutionProvider(
                "CoreML",
                new Dictionary<string, string>
                {
                    ["ModelFormat"] = options.CoreMlModelFormat,
                    ["MLComputeUnits"] = options.CoreMlComputeUnits,
                    ["RequireStaticInputShapes"] = "1",
                    ["EnableOnSubgraphs"] = "0"
                });
            device = $"coreml-{options.CoreMlComputeUnits.ToLowerInvariant()}";
            return sessionOptions;
        }
        catch (Exception exception) when (provider.Equals("Auto", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning(
                exception,
                "CoreML is unavailable; falling back to the CPU execution provider");
            sessionOptions.Dispose();
            device = "cpu";
            return new Microsoft.ML.OnnxRuntime.SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                IntraOpNumThreads = runtime.IntraOpThreads,
                InterOpNumThreads = 1,
                ExecutionMode = ExecutionMode.ORT_SEQUENTIAL
            };
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
        using var pixmap = original.PeekPixels();
        var pngOptions = new SKPngEncoderOptions(
            SKPngEncoderFilterFlags.Sub,
            runtime.PngCompressionLevel);
        if (pixmap is null || !pixmap.Encode(output, pngOptions))
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
        sessionPool?.Writer.TryComplete();
        foreach (var activeSession in sessions)
        {
            activeSession.Dispose();
        }
        prepackedWeights.Dispose();
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
