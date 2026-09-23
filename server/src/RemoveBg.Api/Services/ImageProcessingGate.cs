using System.Threading.RateLimiting;
using RemoveBg.Api.Configuration;
using RemoveBg.Api.Infrastructure;

namespace RemoveBg.Api.Services;

/// <summary>
/// Limits only the expensive image-processing section of a request. Upload parsing,
/// account checks and billing do not hold an inference slot.
/// </summary>
public sealed class ImageProcessingGate : IDisposable
{
    private readonly ConcurrencyLimiter limiter;
    private readonly RemovalRuntimeSettings settings;

    public ImageProcessingGate(RemovalRuntimeSettings settings)
    {
        this.settings = settings;
        limiter = new ConcurrencyLimiter(new ConcurrencyLimiterOptions
        {
            PermitLimit = settings.WorkerCount,
            QueueLimit = settings.QueueCapacity,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst
        });
    }

    public int ActiveCount
    {
        get
        {
            var statistics = limiter.GetStatistics();
            return statistics is null
                ? 0
                : settings.WorkerCount - (int)statistics.CurrentAvailablePermits;
        }
    }

    public int QueuedCount => (int)(limiter.GetStatistics()?.CurrentQueuedCount ?? 0);

    public async ValueTask<RateLimitLease> AcquireAsync(CancellationToken cancellationToken)
    {
        if (settings.QueueWaitSeconds == 0)
        {
            var immediateLease = limiter.AttemptAcquire(1);
            if (immediateLease.IsAcquired)
            {
                return immediateLease;
            }

            immediateLease.Dispose();
            throw Busy();
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.QueueWaitSeconds));

        RateLimitLease lease;
        try
        {
            lease = await limiter.AcquireAsync(1, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw Busy();
        }

        if (lease.IsAcquired)
        {
            return lease;
        }

        lease.Dispose();
        throw Busy();
    }

    private static ApiException Busy() => new(
        StatusCodes.Status429TooManyRequests,
        "server_busy",
        "Все обработчики заняты, повторите запрос позже");

    public void Dispose() => limiter.Dispose();
}
