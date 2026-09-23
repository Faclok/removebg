using System.ComponentModel.DataAnnotations;

namespace RemoveBg.Api.Contracts;

public sealed record RegisterRequest(
    [property: Required, StringLength(200, MinimumLength = 1)] string Name,
    [property: Required, EmailAddress, StringLength(320)] string Email);

public sealed record RegisterResponse(Guid AccountId, string ApiKey, decimal BalanceRub, string Message);

public sealed record CreateApiKeyRequest(
    [property: Required, StringLength(100, MinimumLength = 1)] string Name);

public sealed record ApiKeyCreated(
    Guid Id,
    string Name,
    string ApiKey,
    string Prefix,
    DateTime CreatedAt);

public sealed record ApiKeyView(
    Guid Id,
    string Name,
    string Prefix,
    DateTime CreatedAt,
    DateTime? LastUsedAt,
    DateTime? RevokedAt);

public sealed record PricingTier(int FromImages, int? ToImages, decimal PriceRub);

public sealed record BalanceResponse(
    Guid AccountId,
    decimal BalanceRub,
    int SuccessfulImagesThisMonth,
    decimal NextImagePriceRub,
    IReadOnlyList<PricingTier> Pricing);

public sealed record UsageItem(
    Guid Id,
    Guid RequestId,
    string Status,
    string? Filename,
    long InputBytes,
    long OutputBytes,
    decimal PriceRub,
    long DurationMs,
    string? ErrorCode,
    DateTime CreatedAt);

public sealed record UsageResponse(
    int Total,
    int Successful,
    decimal ChargedRub,
    IReadOnlyList<UsageItem> Items);

public sealed record TopUpRequest(
    [property: Range(typeof(decimal), "0.01", "9999999999.99")] decimal AmountRub,
    [property: StringLength(300)] string Description = "Административное пополнение");

public sealed record TopUpResponse(Guid AccountId, decimal AddedRub, decimal BalanceRub);

public sealed record ErrorEnvelope(ErrorBody Error);
public sealed record ErrorBody(string Code, string Message, Guid? RequestId);
