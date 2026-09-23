using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RemoveBg.Api.Configuration;
using RemoveBg.Api.Contracts;
using RemoveBg.Api.Data;
using RemoveBg.Api.Infrastructure;

namespace RemoveBg.Api.Services;

public sealed class BillingService(
    AppDbContext db,
    IOptions<PricingOptions> pricingOptions)
{
    private readonly PricingOptions pricing = pricingOptions.Value;

    public async Task<int> SuccessfulCountAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var start = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = start.AddMonths(1);
        return await db.UsageRecords.CountAsync(
            x => x.AccountId == accountId
                && x.Status == "success"
                && x.CreatedAt >= start
                && x.CreatedAt < end,
            cancellationToken);
    }

    public long UnitPriceKopecks(int successfulCount) => successfulCount switch
    {
        _ when successfulCount < pricing.Tier1Limit => pricing.Tier1Kopecks,
        _ when successfulCount < pricing.Tier2Limit => pricing.Tier2Kopecks,
        _ when successfulCount < pricing.Tier3Limit => pricing.Tier3Kopecks,
        _ => pricing.Tier4Kopecks
    };

    public IReadOnlyList<PricingTier> PricingTable() =>
    [
        new(0, pricing.Tier1Limit - 1, Rubles(pricing.Tier1Kopecks)),
        new(pricing.Tier1Limit, pricing.Tier2Limit - 1, Rubles(pricing.Tier2Kopecks)),
        new(pricing.Tier2Limit, pricing.Tier3Limit - 1, Rubles(pricing.Tier3Kopecks)),
        new(pricing.Tier3Limit, null, Rubles(pricing.Tier4Kopecks))
    ];

    public async Task RecordFailureAsync(
        Guid requestId,
        Guid accountId,
        Guid apiKeyId,
        string? filename,
        long inputBytes,
        long durationMs,
        string errorCode,
        CancellationToken cancellationToken)
    {
        db.UsageRecords.Add(new UsageRecord
        {
            RequestId = requestId,
            AccountId = accountId,
            ApiKeyId = apiKeyId,
            Status = "failed",
            Filename = filename,
            InputBytes = inputBytes,
            DurationMs = durationMs,
            ErrorCode = errorCode
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<long> ChargeSuccessAsync(
        Guid requestId,
        Guid accountId,
        Guid apiKeyId,
        string? filename,
        long inputBytes,
        long outputBytes,
        long durationMs,
        long priceKopecks,
        CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var updated = await db.Accounts
            .Where(x => x.Id == accountId && x.BalanceKopecks >= priceKopecks)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(
                    x => x.BalanceKopecks,
                    x => x.BalanceKopecks - priceKopecks),
                cancellationToken);
        if (updated != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new ApiException(402, "insufficient_funds", "Недостаточно средств на балансе");
        }

        var balanceAfter = await db.Accounts
            .Where(x => x.Id == accountId)
            .Select(x => x.BalanceKopecks)
            .SingleAsync(cancellationToken);
        var usage = new UsageRecord
        {
            RequestId = requestId,
            AccountId = accountId,
            ApiKeyId = apiKeyId,
            Status = "success",
            Filename = filename,
            InputBytes = inputBytes,
            OutputBytes = outputBytes,
            PriceKopecks = priceKopecks,
            DurationMs = durationMs
        };
        db.UsageRecords.Add(usage);
        db.LedgerEntries.Add(new LedgerEntry
        {
            AccountId = accountId,
            AmountKopecks = -priceKopecks,
            BalanceAfterKopecks = balanceAfter,
            Kind = "usage",
            Description = "Успешное удаление фона",
            UsageId = usage.Id
        });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return balanceAfter;
    }

    public static decimal Rubles(long kopecks) => kopecks / 100m;
}
