namespace RemoveBg.Api.Data;

public sealed class Account
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public required string Email { get; set; }
    public long BalanceKopecks { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public List<ApiKey> ApiKeys { get; set; } = [];
}

public sealed class ApiKey
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AccountId { get; set; }
    public required string Name { get; set; }
    public required string Prefix { get; set; }
    public required string SecretHash { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastUsedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public Account Account { get; set; } = null!;
}

public sealed class UsageRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RequestId { get; set; }
    public Guid AccountId { get; set; }
    public Guid ApiKeyId { get; set; }
    public required string Status { get; set; }
    public string? Filename { get; set; }
    public long InputBytes { get; set; }
    public long OutputBytes { get; set; }
    public long PriceKopecks { get; set; }
    public long DurationMs { get; set; }
    public string? ErrorCode { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class LedgerEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AccountId { get; set; }
    public long AmountKopecks { get; set; }
    public long BalanceAfterKopecks { get; set; }
    public required string Kind { get; set; }
    public required string Description { get; set; }
    public Guid? UsageId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
