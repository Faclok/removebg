using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RemoveBg.Api.Data;
using RemoveBg.Api.Services;

namespace RemoveBg.Api.Tests;

public sealed class ApiFactory : WebApplicationFactory<Program>
{
    private readonly bool failProcessing;
    private readonly IBackgroundRemovalService? removalService;
    private readonly string databasePath;

    public ApiFactory(
        bool failProcessing = false,
        IBackgroundRemovalService? removalService = null)
    {
        this.failProcessing = failProcessing;
        this.removalService = removalService;
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
                ["Admin:Token"] = "test-admin-token"
            });
        });
        builder.ConfigureTestServices(services =>
        {
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

internal sealed class TestRetryingExecutionStrategy(ExecutionStrategyDependencies dependencies)
    : ExecutionStrategy(dependencies, 1, TimeSpan.Zero)
{
    protected override bool ShouldRetryOn(Exception exception) => false;
}

public sealed class BlockingRemovalService : IBackgroundRemovalService
{
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
    private readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool IsReady => true;
    public Task Started => started.Task;

    public Task WarmUpAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task<RemovalResult> RemoveAsync(byte[] input, CancellationToken cancellationToken)
    {
        started.TrySetResult();
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
