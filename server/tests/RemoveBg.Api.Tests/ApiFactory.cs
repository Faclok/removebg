using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RemoveBg.Api.Configuration;
using RemoveBg.Api.Data;
using RemoveBg.Api.Services;

namespace RemoveBg.Api.Tests;

public sealed class ApiFactory : WebApplicationFactory<Program>
{
    private readonly bool failProcessing;
    private readonly IBackgroundRemovalService? removalService;
    private readonly string databasePath;
    private readonly int workerCount;
    private readonly int queueCapacity;
    private readonly int maxAttempts;
    private readonly int processingTimeoutSeconds;
    public FakeAccountEmailSender EmailSender { get; } = new();

    public ApiFactory(
        bool failProcessing = false,
        IBackgroundRemovalService? removalService = null,
        int workerCount = 1,
        int queueCapacity = 1,
        int maxAttempts = 3,
        int processingTimeoutSeconds = 5)
    {
        this.failProcessing = failProcessing;
        this.removalService = removalService;
        this.workerCount = workerCount;
        this.queueCapacity = queueCapacity;
        this.maxAttempts = maxAttempts;
        this.processingTimeoutSeconds = processingTimeoutSeconds;
        var testDirectory = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(testDirectory);
        databasePath = Path.Combine(testDirectory, $"removebg-tests-{Guid.NewGuid():N}.db");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Accounts:StartingBalanceKopecks"] = "100000",
                ["Admin:Token"] = "test-admin-token",
                ["Removal:CpuWorkers"] = workerCount.ToString(),
                ["Removal:IntraOpThreads"] = "1",
                ["Removal:QueueCapacity"] = queueCapacity.ToString(),
                ["Removal:MaxAttempts"] = maxAttempts.ToString(),
                ["Removal:ProcessingTimeoutSeconds"] = processingTimeoutSeconds.ToString(),
                ["Removal:RetryDelaySeconds"] = "0"
            });
        });
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<RemovalRuntimeSettings>();
            services.AddSingleton(new RemovalRuntimeSettings(
                workerCount,
                1,
                queueCapacity,
                1,
                maxAttempts,
                processingTimeoutSeconds,
                0));
            services.RemoveAll<IDbContextOptionsConfiguration<AppDbContext>>();
            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.AddDbContext<AppDbContext>(options =>
                options.UseSqlite(
                    $"Data Source={databasePath};Pooling=False",
                    sqlite => sqlite.ExecutionStrategy(
                        dependencies => new TestRetryingExecutionStrategy(dependencies))));
            services.RemoveAll<IBackgroundRemovalService>();
            services.AddSingleton(
                removalService ?? new FakeRemovalService(failProcessing));
            services.RemoveAll<IAccountEmailSender>();
            services.AddSingleton<IAccountEmailSender>(EmailSender);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        foreach (var suffix in new[] { string.Empty, "-shm", "-wal" })
        {
            var path = databasePath + suffix;
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}

public sealed class FakeAccountEmailSender : IAccountEmailSender
{
    private readonly ConcurrentQueue<SentRegistrationEmail> sent = new();

    public bool FailDelivery { get; set; }
    public IReadOnlyCollection<SentRegistrationEmail> Sent => sent.ToArray();

    public Task SendRegistrationPasswordAsync(
        string recipientName,
        string recipientEmail,
        string password,
        CancellationToken cancellationToken)
    {
        if (FailDelivery)
        {
            throw new EmailDeliveryException("Test email failure");
        }

        sent.Enqueue(new SentRegistrationEmail(recipientName, recipientEmail, password));
        return Task.CompletedTask;
    }

    public string PasswordFor(string email) =>
        sent.Last(x => x.RecipientEmail == email).Password;
}

public sealed record SentRegistrationEmail(
    string RecipientName,
    string RecipientEmail,
    string Password);

internal sealed class TestRetryingExecutionStrategy(ExecutionStrategyDependencies dependencies)
    : ExecutionStrategy(dependencies, 1, TimeSpan.Zero)
{
    protected override bool ShouldRetryOn(Exception exception) => false;
}

public sealed class BlockingRemovalService(int expectedStarts = 1) : IBackgroundRemovalService
{
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
    private readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int startedCount;

    public bool IsReady => true;
    public string Device => "test";
    public Task Started => started.Task;

    public Task WarmUpAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task<RemovalResult> RemoveAsync(byte[] input, CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref startedCount) >= expectedStarts)
        {
            started.TrySetResult();
        }
        await released.Task.WaitAsync(cancellationToken);
        return new RemovalResult(Png, 1, 1, 1);
    }

    public void Release() => released.TrySetResult();
}

public sealed class FakeRemovalService(bool failProcessing) : IBackgroundRemovalService
{
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    public bool IsReady => true;
    public string Device => "test";

    public Task WarmUpAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<RemovalResult> RemoveAsync(byte[] input, CancellationToken cancellationToken)
    {
        if (failProcessing)
        {
            throw new Infrastructure.ApiException(503, "processing_failed", "Тестовая ошибка");
        }
        return Task.FromResult(new RemovalResult(Png, 1, 1, 1));
    }
}

public sealed class FlakyRemovalService(int failuresBeforeSuccess) : IBackgroundRemovalService
{
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
    private int calls;

    public bool IsReady => true;
    public string Device => "test";
    public int Calls => Volatile.Read(ref calls);

    public Task WarmUpAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<RemovalResult> RemoveAsync(byte[] input, CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref calls) <= failuresBeforeSuccess)
        {
            throw new InvalidOperationException("Transient worker failure");
        }
        return Task.FromResult(new RemovalResult(Png, 1, 1, 7, 0.95));
    }
}

public sealed class TimeoutRemovalService : IBackgroundRemovalService
{
    private int calls;
    public bool IsReady => true;
    public string Device => "test";
    public int Calls => Volatile.Read(ref calls);
    public Task WarmUpAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task<RemovalResult> RemoveAsync(byte[] input, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref calls);
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        throw new InvalidOperationException("Unreachable");
    }
}
