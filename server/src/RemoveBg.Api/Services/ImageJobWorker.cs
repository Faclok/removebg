using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using RemoveBg.Api.Configuration;
using RemoveBg.Api.Data;
using RemoveBg.Api.Infrastructure;

namespace RemoveBg.Api.Services;

public sealed class ImageJobWorker(
    ImageJobQueue queue,
    IServiceScopeFactory scopeFactory,
    RemovalRuntimeSettings settings,
    ILogger<ImageJobWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workers = Enumerable.Range(0, settings.WorkerCount)
            .Select(workerId => ConsumeAsync(workerId, stoppingToken))
            .ToArray();

        await RecoverInterruptedJobsAsync(stoppingToken);
        await Task.WhenAll(workers);
    }

    private async Task RecoverInterruptedJobsAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.ImageJobs
            .Where(x => x.Status == "processing")
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(x => x.Status, "queued"),
                cancellationToken);
        var pending = await db.ImageJobs.AsNoTracking()
            .Where(x => x.Status == "queued")
            .OrderBy(x => x.CreatedAt)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);
        foreach (var jobId in pending)
        {
            await queue.EnqueueAsync(jobId, cancellationToken);
        }
    }

    private async Task ConsumeAsync(int workerId, CancellationToken stoppingToken)
    {
        await foreach (var jobId in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                using (queue.BeginProcessing())
                {
                    await ProcessJobAsync(jobId, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Worker {WorkerId} crashed while handling job {JobId}", workerId, jobId);
            }
        }
    }

    private async Task ProcessJobAsync(Guid jobId, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var removal = scope.ServiceProvider.GetRequiredService<IBackgroundRemovalService>();
            var billing = scope.ServiceProvider.GetRequiredService<BillingService>();
            var job = await db.ImageJobs.SingleOrDefaultAsync(x => x.Id == jobId, stoppingToken);
            if (job is null || job.Status is "success" or "failed")
            {
                return;
            }

            job.Status = "processing";
            job.StartedAt ??= DateTime.UtcNow;
            job.QueueDurationMs = Math.Max(
                0,
                (long)(job.StartedAt.Value - job.CreatedAt).TotalMilliseconds);
            job.AttemptCount++;
            job.ErrorCode = null;
            job.ErrorMessage = null;
            await db.SaveChangesAsync(stoppingToken);

            try
            {
                var measured = await MeasureAsync(
                    token => removal.RemoveAsync(job.InputData, token),
                    TimeSpan.FromSeconds(settings.ProcessingTimeoutSeconds),
                    stoppingToken);
                var processingDurationMs = job.ProcessingDurationMs + measured.ElapsedMs;
                var cpuTimeMs = job.CpuTimeMs + measured.CpuTimeMs;
                var peakRamBytes = Math.Max(job.PeakRamBytes, measured.PeakRamBytes);

                var balanceAfter = await billing.ChargeSuccessAsync(
                    job.RequestId,
                    job.AccountId,
                    job.ApiKeyId,
                    job.Filename,
                    job.InputBytes,
                    measured.Value.Png.Length,
                    processingDurationMs,
                    job.PriceKopecks,
                    stoppingToken);

                job = await db.ImageJobs.SingleAsync(x => x.Id == jobId, stoppingToken);
                job.Status = "success";
                job.OutputData = measured.Value.Png;
                job.InputData = [];
                job.OutputBytes = measured.Value.Png.Length;
                job.Width = measured.Value.Width;
                job.Height = measured.Value.Height;
                job.BalanceAfterKopecks = balanceAfter;
                job.QualityScore = measured.Value.QualityScore;
                job.ProcessingDurationMs = processingDurationMs;
                job.CpuTimeMs = cpuTimeMs;
                job.PeakRamBytes = peakRamBytes;
                job.CompletedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(stoppingToken);
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                var measuredFailure = exception as MeasurementException;
                var cause = measuredFailure?.InnerException ?? exception;
                var (code, message, transient) = Classify(cause);
                logger.LogWarning(
                    exception,
                    "Job {JobId} attempt {Attempt}/{MaxAttempts} failed with {ErrorCode}",
                    job.Id,
                    job.AttemptCount,
                    job.MaxAttempts,
                    code);

                db.ChangeTracker.Clear();
                job = await db.ImageJobs.SingleAsync(x => x.Id == jobId, stoppingToken);
                if (measuredFailure is not null)
                {
                    job.ProcessingDurationMs += measuredFailure.ElapsedMs;
                    job.CpuTimeMs += measuredFailure.CpuTimeMs;
                    job.PeakRamBytes = Math.Max(job.PeakRamBytes, measuredFailure.PeakRamBytes);
                }
                job.ErrorCode = code;
                job.ErrorMessage = message;
                if (transient && job.AttemptCount < job.MaxAttempts)
                {
                    job.Status = "processing";
                    await db.SaveChangesAsync(stoppingToken);
                    if (settings.RetryDelaySeconds > 0)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(settings.RetryDelaySeconds), stoppingToken);
                    }
                    continue;
                }

                job.Status = "failed";
                job.InputData = [];
                job.CompletedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(stoppingToken);
                await billing.RecordFailureAsync(
                    job.RequestId,
                    job.AccountId,
                    job.ApiKeyId,
                    job.Filename,
                    job.InputBytes,
                    job.ProcessingDurationMs,
                    code,
                    stoppingToken);
                return;
            }
        }
    }

    private static (string Code, string Message, bool Transient) Classify(Exception exception) => exception switch
    {
        TimeoutException => ("processing_timeout", "Превышено время обработки", true),
        ApiException apiException => (
            apiException.Code,
            apiException.Message,
            apiException.StatusCode >= 500),
        _ => ("worker_failure", "Обработчик задания аварийно завершился", true)
    };

    private static async Task<Measurement<T>> MeasureAsync<T>(
        Func<CancellationToken, Task<T>> action,
        TimeSpan timeout,
        CancellationToken stoppingToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeoutSource.CancelAfter(timeout);
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var cpuStart = process.TotalProcessorTime;
        var peakRam = process.WorkingSet64;
        var stopwatch = Stopwatch.StartNew();
        using var samplingSource = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var sampler = Task.Run(async () =>
        {
            try
            {
                using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
                while (await timer.WaitForNextTickAsync(samplingSource.Token))
                {
                    process.Refresh();
                    peakRam = Math.Max(peakRam, process.WorkingSet64);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when the measured operation ends.
            }
        }, CancellationToken.None);

        try
        {
            var operation = action(timeoutSource.Token);
            var value = await operation.WaitAsync(timeoutSource.Token);
            process.Refresh();
            return new Measurement<T>(
                value,
                stopwatch.ElapsedMilliseconds,
                Math.Max(0, (long)(process.TotalProcessorTime - cpuStart).TotalMilliseconds),
                Math.Max(peakRam, process.WorkingSet64));
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            process.Refresh();
            throw new MeasurementException(
                new TimeoutException("Image processing timed out"),
                stopwatch.ElapsedMilliseconds,
                Math.Max(0, (long)(process.TotalProcessorTime - cpuStart).TotalMilliseconds),
                Math.Max(peakRam, process.WorkingSet64));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            process.Refresh();
            throw new MeasurementException(
                exception,
                stopwatch.ElapsedMilliseconds,
                Math.Max(0, (long)(process.TotalProcessorTime - cpuStart).TotalMilliseconds),
                Math.Max(peakRam, process.WorkingSet64));
        }
        finally
        {
            samplingSource.Cancel();
            await sampler;
        }
    }

    private sealed record Measurement<T>(T Value, long ElapsedMs, long CpuTimeMs, long PeakRamBytes);

    private sealed class MeasurementException(
        Exception innerException,
        long elapsedMs,
        long cpuTimeMs,
        long peakRamBytes) : Exception(innerException.Message, innerException)
    {
        public long ElapsedMs { get; } = elapsedMs;
        public long CpuTimeMs { get; } = cpuTimeMs;
        public long PeakRamBytes { get; } = peakRamBytes;
    }
}
