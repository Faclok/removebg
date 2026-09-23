namespace RemoveBg.Api.Configuration;

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";
    public string ConnectionString { get; init; } =
        "Server=localhost;Port=3306;Database=removebg;User=removebg;Password=removebg";
}

public sealed class AccountOptions
{
    public const string SectionName = "Accounts";
    public long StartingBalanceKopecks { get; init; } = 100_000;
}

public sealed class RemovalOptions
{
    public const string SectionName = "Removal";
    public string ModelPath { get; init; } = "models/birefnet-general-lite.onnx";
    public string ModelUrl { get; init; } = string.Empty;
    public string ModelMd5 { get; init; } = string.Empty;
    public int InputSize { get; init; } = 1024;
    public long MaxUploadBytes { get; init; } = 15 * 1024 * 1024;
    public long MaxImagePixels { get; init; } = 40_000_000;
    public int CpuWorkers { get; init; } = 1;
    public int IntraOpThreads { get; init; } = 4;
    public int QueueWaitSeconds { get; init; } = 30;
}

public sealed class PricingOptions
{
    public const string SectionName = "Pricing";
    public int Tier1Limit { get; init; } = 10_000;
    public long Tier1Kopecks { get; init; } = 60;
    public int Tier2Limit { get; init; } = 100_000;
    public long Tier2Kopecks { get; init; } = 45;
    public int Tier3Limit { get; init; } = 500_000;
    public long Tier3Kopecks { get; init; } = 30;
    public long Tier4Kopecks { get; init; } = 20;
}

public sealed class AdminOptions
{
    public const string SectionName = "Admin";
    public string Token { get; init; } = string.Empty;
}
