using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RemoveBg.Api.Contracts;
using RemoveBg.Api.Data;
using RemoveBg.Api.Infrastructure;

namespace RemoveBg.Api.Security;

public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    AppDbContext db)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string AuthenticationSchemeName = "ApiKey";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var raw = Request.Headers["X-API-Key"].FirstOrDefault();
        var authorization = Request.Headers.Authorization.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(raw)
            && authorization?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true)
        {
            raw = authorization[7..].Trim();
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            return AuthenticateResult.NoResult();
        }

        var hash = ApiKeyService.Hash(raw);
        var apiKey = await db.ApiKeys
            .Include(x => x.Account)
            .SingleOrDefaultAsync(x => x.SecretHash == hash && x.RevokedAt == null, Context.RequestAborted);
        if (apiKey is null)
        {
            return AuthenticateResult.Fail("Invalid API key");
        }

        apiKey.LastUsedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(Context.RequestAborted);

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, apiKey.AccountId.ToString()),
            new Claim(RequestContext.ApiKeyIdClaim, apiKey.Id.ToString()),
            new Claim(ClaimTypes.Name, apiKey.Account.Name)
        };
        var identity = new ClaimsIdentity(claims, AuthenticationSchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), AuthenticationSchemeName);
        return AuthenticateResult.Success(ticket);
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = (int)HttpStatusCode.Unauthorized;
        var code = Request.Headers.ContainsKey("X-API-Key") || Request.Headers.ContainsKey("Authorization")
            ? "invalid_api_key"
            : "missing_api_key";
        var message = code == "invalid_api_key"
            ? "API-ключ недействителен или отозван"
            : "Передайте ключ в X-API-Key или Bearer token";
        await Response.WriteAsJsonAsync(
            new ErrorEnvelope(new ErrorBody(code, message, Context.GetRequestId())));
    }
}
