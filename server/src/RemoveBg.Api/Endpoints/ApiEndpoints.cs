using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RemoveBg.Api.Configuration;
using RemoveBg.Api.Contracts;
using RemoveBg.Api.Data;
using RemoveBg.Api.Infrastructure;
using RemoveBg.Api.Security;
using RemoveBg.Api.Services;

namespace RemoveBg.Api.Endpoints;

public static class ApiEndpoints
{
    private static readonly HashSet<string> AllowedContentTypes =
    [
        "image/jpeg",
        "image/png",
        "image/webp"
    ];

    public static IEndpointRouteBuilder MapApiEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/v1").WithTags("API");

        api.MapPost("/accounts/register", RegisterAsync)
            .WithName("RegisterAccount")
            .WithSummary("Создать аккаунт и получить первый API-ключ")
            .Produces<RegisterResponse>(201)
            .Produces<ErrorEnvelope>(409)
            .AllowAnonymous();

        var authorized = api.MapGroup(string.Empty).RequireAuthorization();
        authorized.MapGet("/balance", GetBalanceAsync)
            .WithName("GetBalance")
            .WithSummary("Получить баланс и текущую цену")
            .Produces<BalanceResponse>();
        authorized.MapPost("/remove-background", RemoveBackgroundAsync)
            .WithName("RemoveBackground")
            .WithSummary("Удалить фон изображения")
            .Accepts<IFormFile>("multipart/form-data")
            .Produces<byte[]>(200, contentType: "image/png")
            .Produces<ErrorEnvelope>(402)
            .Produces<ErrorEnvelope>(413)
            .Produces<ErrorEnvelope>(415)
            .Produces<ErrorEnvelope>(422)
            .Produces<ErrorEnvelope>(429)
            .Produces<ErrorEnvelope>(503)
            .DisableAntiforgery();
        authorized.MapGet("/usage", GetUsageAsync)
            .WithName("GetUsage")
            .WithSummary("Получить статистику и журнал обработок")
            .Produces<UsageResponse>();
        authorized.MapGet("/api-keys", ListApiKeysAsync)
            .WithName("ListApiKeys")
            .Produces<IReadOnlyList<ApiKeyView>>();
        authorized.MapPost("/api-keys", CreateApiKeyAsync)
            .WithName("CreateApiKey")
            .Produces<ApiKeyCreated>(201);
        authorized.MapDelete("/api-keys/{keyId:guid}", RevokeApiKeyAsync)
            .WithName("RevokeApiKey")
            .Produces(204)
            .Produces<ErrorEnvelope>(404);

        api.MapPost("/admin/accounts/{accountId:guid}/top-up", TopUpAsync)
            .WithName("AdminTopUp")
            .WithSummary("Пополнить баланс вручную")
            .Produces<TopUpResponse>()
            .Produces<ErrorEnvelope>(401)
            .Produces<ErrorEnvelope>(404)
            .Produces<ErrorEnvelope>(503)
            .AllowAnonymous();

        return app;
    }

    private static async Task<IResult> RegisterAsync(
        RegisterRequest request,
        AppDbContext db,
        IOptions<AccountOptions> options,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name)
            || string.IsNullOrWhiteSpace(request.Email)
            || !new System.ComponentModel.DataAnnotations.EmailAddressAttribute().IsValid(request.Email))
        {
            throw new ApiException(422, "validation_error", "Укажите корректные имя и email");
        }

        var email = request.Email.Trim().ToLowerInvariant();
        if (await db.Accounts.AnyAsync(x => x.Email == email, cancellationToken))
        {
            throw new ApiException(409, "email_exists", "Аккаунт с таким email уже существует");
        }

        var issued = ApiKeyService.Issue();
        var account = new Account
        {
            Name = request.Name.Trim(),
            Email = email,
            BalanceKopecks = options.Value.StartingBalanceKopecks
        };
        db.Accounts.Add(account);
        db.ApiKeys.Add(new ApiKey
        {
            AccountId = account.Id,
            Name = "Первый ключ",
            Prefix = issued.Prefix,
            SecretHash = issued.Hash
        });
        db.LedgerEntries.Add(new LedgerEntry
        {
            AccountId = account.Id,
            AmountKopecks = account.BalanceKopecks,
            BalanceAfterKopecks = account.BalanceKopecks,
            Kind = "welcome_bonus",
            Description = "Стартовый баланс"
        });

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            throw new ApiException(409, "email_exists", "Аккаунт с таким email уже существует");
        }

        return Results.Created(
            $"/v1/balance",
            new RegisterResponse(
                account.Id,
                issued.Raw,
                BillingService.Rubles(account.BalanceKopecks),
                "Сохраните API-ключ: повторно он показан не будет"));
    }

    private static async Task<IResult> GetBalanceAsync(
        HttpContext context,
        AppDbContext db,
        BillingService billing,
        CancellationToken cancellationToken)
    {
        var accountId = context.User.GetAccountId();
        var account = await db.Accounts.AsNoTracking().SingleAsync(x => x.Id == accountId, cancellationToken);
        var count = await billing.SuccessfulCountAsync(accountId, cancellationToken);
        return Results.Ok(new BalanceResponse(
            accountId,
            BillingService.Rubles(account.BalanceKopecks),
            count,
            BillingService.Rubles(billing.UnitPriceKopecks(count)),
            billing.PricingTable()));
    }

    private static async Task<IResult> RemoveBackgroundAsync(
        HttpContext context,
        IFormFile image,
        AppDbContext db,
        BillingService billing,
        IBackgroundRemovalService removal,
        ImageProcessingGate processingGate,
        ILogger<Program> logger,
        IOptions<RemovalOptions> options,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();
        var accountId = context.User.GetAccountId();
        var apiKeyId = context.User.GetApiKeyId();
        var requestId = context.GetRequestId();

        if (context.Request.Form.Files.Count != 1)
        {
            throw new ApiException(422, "single_image_required", "Один запрос должен содержать ровно одно изображение");
        }
        if (!AllowedContentTypes.Contains(image.ContentType.ToLowerInvariant()))
        {
            throw new ApiException(415, "unsupported_media_type", "Поддерживаются JPEG, PNG и WebP");
        }
        if (image.Length <= 0)
        {
            throw new ApiException(422, "empty_file", "Получен пустой файл");
        }
        if (image.Length > options.Value.MaxUploadBytes)
        {
            throw new ApiException(413, "file_too_large", "Файл превышает допустимый размер");
        }

        var count = await billing.SuccessfulCountAsync(accountId, cancellationToken);
        var price = billing.UnitPriceKopecks(count);
        var balance = await db.Accounts
            .Where(x => x.Id == accountId)
            .Select(x => x.BalanceKopecks)
            .SingleAsync(cancellationToken);
        if (balance < price)
        {
            throw new ApiException(402, "insufficient_funds", "Недостаточно средств на балансе");
        }

        var input = GC.AllocateUninitializedArray<byte>((int)image.Length);
        await using (var stream = image.OpenReadStream())
        {
            await stream.ReadExactlyAsync(input.AsMemory(), cancellationToken);
        }

        RemovalResult result;
        var processingLease = await processingGate.AcquireAsync(cancellationToken);
        try
        {
            using (processingLease)
            {
                result = await removal.RemoveAsync(input, cancellationToken);
            }
        }
        catch (ApiException exception)
        {
            await billing.RecordFailureAsync(
                requestId,
                accountId,
                apiKeyId,
                image.FileName,
                input.Length,
                started.ElapsedMilliseconds,
                exception.Code,
                cancellationToken);
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Image processing failed for request {RequestId}",
                requestId);
            await billing.RecordFailureAsync(
                requestId,
                accountId,
                apiKeyId,
                image.FileName,
                input.Length,
                started.ElapsedMilliseconds,
                "processing_failed",
                cancellationToken);
            throw new ApiException(503, "processing_failed", "Не удалось обработать изображение")
            {
                Source = exception.Source
            };
        }

        var balanceAfter = await billing.ChargeSuccessAsync(
            requestId,
            accountId,
            apiKeyId,
            image.FileName,
            input.Length,
            result.Png.Length,
            result.DurationMs,
            price,
            cancellationToken);
        context.Response.Headers["X-Image-Width"] = result.Width.ToString();
        context.Response.Headers["X-Image-Height"] = result.Height.ToString();
        context.Response.Headers["X-Processing-Duration-Ms"] = result.DurationMs.ToString();
        context.Response.Headers["Server-Timing"] = $"image;dur={result.DurationMs}";
        context.Response.Headers["X-Charged-Rub"] = BillingService.Rubles(price)
            .ToString("0.00", CultureInfo.InvariantCulture);
        context.Response.Headers["X-Balance-Rub"] = BillingService.Rubles(balanceAfter)
            .ToString("0.00", CultureInfo.InvariantCulture);
        return Results.File(result.Png, "image/png", "background-removed.png");
    }

    private static async Task<IResult> GetUsageAsync(
        HttpContext context,
        AppDbContext db,
        int limit = 50,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 200);
        offset = Math.Max(offset, 0);
        var accountId = context.User.GetAccountId();
        var query = db.UsageRecords.AsNoTracking().Where(x => x.AccountId == accountId);
        var total = await query.CountAsync(cancellationToken);
        var successful = await query.CountAsync(x => x.Status == "success", cancellationToken);
        var charged = await query.SumAsync(x => x.PriceKopecks, cancellationToken);
        var records = await query.OrderByDescending(x => x.CreatedAt)
            .Skip(offset)
            .Take(limit)
            .Select(x => new UsageItem(
                x.Id,
                x.RequestId,
                x.Status,
                x.Filename,
                x.InputBytes,
                x.OutputBytes,
                BillingService.Rubles(x.PriceKopecks),
                x.DurationMs,
                x.ErrorCode,
                x.CreatedAt))
            .ToListAsync(cancellationToken);
        return Results.Ok(new UsageResponse(total, successful, BillingService.Rubles(charged), records));
    }

    private static async Task<IResult> ListApiKeysAsync(
        HttpContext context,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var accountId = context.User.GetAccountId();
        var keys = await db.ApiKeys.AsNoTracking()
            .Where(x => x.AccountId == accountId)
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => new ApiKeyView(
                x.Id,
                x.Name,
                x.Prefix,
                x.CreatedAt,
                x.LastUsedAt,
                x.RevokedAt))
            .ToListAsync(cancellationToken);
        return Results.Ok(keys);
    }

    private static async Task<IResult> CreateApiKeyAsync(
        HttpContext context,
        CreateApiKeyRequest request,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new ApiException(422, "validation_error", "Укажите название API-ключа");
        }
        var issued = ApiKeyService.Issue();
        var key = new ApiKey
        {
            AccountId = context.User.GetAccountId(),
            Name = request.Name.Trim(),
            Prefix = issued.Prefix,
            SecretHash = issued.Hash
        };
        db.ApiKeys.Add(key);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Created(
            $"/v1/api-keys/{key.Id}",
            new ApiKeyCreated(key.Id, key.Name, issued.Raw, key.Prefix, key.CreatedAt));
    }

    private static async Task<IResult> RevokeApiKeyAsync(
        HttpContext context,
        Guid keyId,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var accountId = context.User.GetAccountId();
        var key = await db.ApiKeys.SingleOrDefaultAsync(
            x => x.Id == keyId && x.AccountId == accountId,
            cancellationToken);
        if (key is null)
        {
            throw new ApiException(404, "api_key_not_found", "API-ключ не найден");
        }
        key.RevokedAt ??= DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> TopUpAsync(
        HttpContext context,
        Guid accountId,
        TopUpRequest request,
        AppDbContext db,
        IOptions<AdminOptions> options,
        CancellationToken cancellationToken)
    {
        var configuredToken = options.Value.Token;
        if (string.IsNullOrWhiteSpace(configuredToken))
        {
            throw new ApiException(503, "admin_disabled", "Административный API отключён");
        }
        var suppliedToken = context.Request.Headers["X-Admin-Token"].FirstOrDefault();
        if (!FixedTimeEquals(suppliedToken, configuredToken))
        {
            throw new ApiException(401, "invalid_admin_token", "Неверный административный токен");
        }
        if (request.AmountRub <= 0 || request.AmountRub > 9_999_999_999.99m)
        {
            throw new ApiException(422, "validation_error", "Сумма пополнения некорректна");
        }

        var account = await db.Accounts.SingleOrDefaultAsync(x => x.Id == accountId, cancellationToken);
        if (account is null)
        {
            throw new ApiException(404, "account_not_found", "Аккаунт не найден");
        }
        var amount = checked((long)decimal.Round(request.AmountRub * 100m, 0, MidpointRounding.AwayFromZero));
        account.BalanceKopecks += amount;
        db.LedgerEntries.Add(new LedgerEntry
        {
            AccountId = accountId,
            AmountKopecks = amount,
            BalanceAfterKopecks = account.BalanceKopecks,
            Kind = "top_up",
            Description = request.Description
        });
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(new TopUpResponse(
            accountId,
            BillingService.Rubles(amount),
            BillingService.Rubles(account.BalanceKopecks)));
    }

    private static bool FixedTimeEquals(string? supplied, string expected)
    {
        if (supplied is null)
        {
            return false;
        }
        var suppliedBytes = Encoding.UTF8.GetBytes(supplied);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        return suppliedBytes.Length == expectedBytes.Length
            && CryptographicOperations.FixedTimeEquals(suppliedBytes, expectedBytes);
    }
}
