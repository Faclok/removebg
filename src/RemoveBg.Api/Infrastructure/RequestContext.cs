using System.Security.Claims;

namespace RemoveBg.Api.Infrastructure;

public static class RequestContext
{
    public const string RequestIdItem = "RequestId";
    public const string ApiKeyIdClaim = "api_key_id";

    public static Guid GetRequestId(this HttpContext context) =>
        context.Items.TryGetValue(RequestIdItem, out var value) && value is Guid id ? id : Guid.Empty;

    public static Guid GetAccountId(this ClaimsPrincipal principal) =>
        Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public static Guid GetApiKeyId(this ClaimsPrincipal principal) =>
        Guid.Parse(principal.FindFirstValue(ApiKeyIdClaim)!);
}
