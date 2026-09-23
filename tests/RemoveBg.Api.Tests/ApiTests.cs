using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using SkiaSharp;
using Xunit;

namespace RemoveBg.Api.Tests;

public sealed class ApiTests
{
    [Fact]
    public void SkiaSharpDecodesPngOnCurrentPlatform()
    {
        using var bitmap = SKBitmap.Decode(SamplePng());
        Assert.NotNull(bitmap);
        Assert.Equal(1, bitmap.Width);
        Assert.Equal(1, bitmap.Height);
    }

    [Fact]
    public async Task RegistrationCreatesApiKeyAndOneThousandRubles()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        var account = await RegisterAsync(client);

        Assert.StartsWith("rb_live_", account.ApiKey);
        Assert.Equal(1000.00m, account.BalanceRub);

        client.DefaultRequestHeaders.Add("X-API-Key", account.ApiKey);
        var balance = await client.GetFromJsonAsync<BalanceDto>("/v1/balance");
        Assert.NotNull(balance);
        Assert.Equal(1000.00m, balance.BalanceRub);
        Assert.Equal(0.60m, balance.NextImagePriceRub);
    }

    [Fact]
    public async Task SuccessfulRemovalChargesSixtyKopecks()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        var account = await RegisterAsync(client);
        client.DefaultRequestHeaders.Add("X-API-Key", account.ApiKey);

        using var form = ImageForm();
        var response = await client.PostAsync("/v1/remove-background", form);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("0.60", response.Headers.GetValues("X-Charged-Rub").Single());

        var balance = await client.GetFromJsonAsync<BalanceDto>("/v1/balance");
        Assert.Equal(999.40m, balance!.BalanceRub);
        var usage = await client.GetFromJsonAsync<UsageDto>("/v1/usage");
        Assert.Equal(1, usage!.Successful);
        Assert.Equal(0.60m, usage.ChargedRub);
    }

    [Fact]
    public async Task ProcessingFailureDoesNotChargeBalance()
    {
        using var factory = new ApiFactory(failProcessing: true);
        using var client = factory.CreateClient();
        var account = await RegisterAsync(client, "failure@example.ru");
        client.DefaultRequestHeaders.Add("X-API-Key", account.ApiKey);

        using var form = ImageForm();
        var response = await client.PostAsync("/v1/remove-background", form);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        var balance = await client.GetFromJsonAsync<BalanceDto>("/v1/balance");
        Assert.Equal(1000.00m, balance!.BalanceRub);
        var usage = await client.GetFromJsonAsync<UsageDto>("/v1/usage");
        Assert.Equal(0, usage!.Successful);
        Assert.Equal(0m, usage.ChargedRub);
    }

    [Fact]
    public async Task InvalidKeyAndDuplicateEmailAreRejected()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        await RegisterAsync(client);

        var duplicate = await client.PostAsJsonAsync(
            "/v1/accounts/register",
            new { name = "Дубль", email = "test@example.ru" });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        client.DefaultRequestHeaders.Add("X-API-Key", "wrong");
        var unauthorized = await client.GetAsync("/v1/balance");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
    }

    [Fact]
    public async Task AdditionalKeyAndAdminTopUpWork()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        var account = await RegisterAsync(client);
        client.DefaultRequestHeaders.Add("X-API-Key", account.ApiKey);

        var created = await client.PostAsJsonAsync("/v1/api-keys", new { name = "mobile-app" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var second = await created.Content.ReadFromJsonAsync<KeyDto>();
        Assert.StartsWith("rb_live_", second!.ApiKey);

        client.DefaultRequestHeaders.Remove("X-API-Key");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", second.ApiKey);
        var keys = await client.GetFromJsonAsync<JsonElement>("/v1/api-keys");
        Assert.Equal(2, keys.GetArrayLength());

        client.DefaultRequestHeaders.Authorization = null;
        client.DefaultRequestHeaders.Add("X-Admin-Token", "test-admin-token");
        var topUp = await client.PostAsJsonAsync(
            $"/v1/admin/accounts/{account.AccountId}/top-up",
            new { amountRub = 2000.00m, description = "Тест" });
        Assert.Equal(HttpStatusCode.OK, topUp.StatusCode);
        var result = await topUp.Content.ReadFromJsonAsync<TopUpDto>();
        Assert.Equal(3000.00m, result!.BalanceRub);
    }

    [Fact]
    public async Task SwaggerContainsApiKeySchemeAndPngResponse()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        var document = await client.GetFromJsonAsync<JsonElement>("/swagger/v1/swagger.json");

        Assert.True(document.GetProperty("components").GetProperty("securitySchemes")
            .TryGetProperty("ApiKeyAuth", out _));
        Assert.Equal(0, document.GetProperty("paths").GetProperty("/v1/accounts/register")
            .GetProperty("post").GetProperty("security").GetArrayLength());
        Assert.True(document.GetProperty("paths").GetProperty("/v1/remove-background")
            .GetProperty("post").GetProperty("responses").GetProperty("200")
            .GetProperty("content").TryGetProperty("image/png", out _));
    }

    private static async Task<RegisterDto> RegisterAsync(
        HttpClient client,
        string email = "test@example.ru")
    {
        var response = await client.PostAsJsonAsync(
            "/v1/accounts/register",
            new { name = "ООО Тест", email });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<RegisterDto>())!;
    }

    private static MultipartFormDataContent ImageForm()
    {
        var content = new ByteArrayContent(SamplePng());
        content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        var form = new MultipartFormDataContent();
        form.Add(content, "image", "product.png");
        return form;
    }

    private static byte[] SamplePng() => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    private sealed record RegisterDto(Guid AccountId, string ApiKey, decimal BalanceRub);
    private sealed record BalanceDto(decimal BalanceRub, decimal NextImagePriceRub);
    private sealed record UsageDto(int Successful, decimal ChargedRub);
    private sealed record KeyDto(string ApiKey);
    private sealed record TopUpDto(decimal BalanceRub);
}
