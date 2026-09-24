using System.Threading.Channels;
using RemoveBg.Api.Configuration;

namespace RemoveBg.Api.Services;

public sealed class ImageJobQueue
{
    private readonly Channel<Guid> channel;
    private int queuedCount;
    private int activeCount;

    public ImageJobQueue(RemovalRuntimeSettings settings)
    {
        channel = Channel.CreateBounded<Guid>(new BoundedChannelOptions(Math.Max(1, settings.QueueCapacity))
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
    }

    public int QueuedCount => Volatile.Read(ref queuedCount);
    public int ActiveCount => Volatile.Read(ref activeCount);

    public bool TryEnqueue(Guid jobId)
    {
        Interlocked.Increment(ref queuedCount);
        if (!channel.Writer.TryWrite(jobId))
        {
            Interlocked.Decrement(ref queuedCount);
            return false;
        }
        return true;
    }

    public async ValueTask EnqueueAsync(Guid jobId, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref queuedCount);
        try
        {
            await channel.Writer.WriteAsync(jobId, cancellationToken);
        }
        catch
        {
            Interlocked.Decrement(ref queuedCount);
            throw;
        }
    }

    public async IAsyncEnumerable<Guid> ReadAllAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var jobId in channel.Reader.ReadAllAsync(cancellationToken))
        {
            Interlocked.Decrement(ref queuedCount);
            yield return jobId;
        }
    }

    public IDisposable BeginProcessing()
    {
        Interlocked.Increment(ref activeCount);
        return new CounterLease(() => Interlocked.Decrement(ref activeCount));
    }

    private sealed class CounterLease(Action release) : IDisposable
    {
        private Action? releaseAction = release;
        public void Dispose() => Interlocked.Exchange(ref releaseAction, null)?.Invoke();
    }
}
