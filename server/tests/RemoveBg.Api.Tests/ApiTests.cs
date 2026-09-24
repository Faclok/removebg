using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using RemoveBg.Api.Configuration;
using RemoveBg.Api.Infrastructure;
using RemoveBg.Api.Services;
using SkiaSharp;
using Xunit;

namespace RemoveBg.Api.Tests;

public sealed class ApiTests
{
    [Fact]
    public void SkiaSharpDecodesPngOnCurrentPlatform()
    {
        using var bitmap = SKBitmap.Decode(
            SamplePng(),
            new SKImageInfo(1, 1, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        Assert.NotNull(bitmap);
        Assert.Equal(1, bitmap.Width);
        Assert.Equal(1, bitmap.Height);
    }

    [Fact]
    public void NativeMaskCompositeAppliesAlphaWithoutChangingOpaqueColor()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(
            2,
            1,
            SKColorType.Rgba8888,
            SKAlphaType.Unpremul));
        bitmap.SetPixel(0, 0, SKColors.Red);
        bitmap.SetPixel(1, 0, SKColors.Blue);

        BackgroundRemovalService.ApplyMask(bitmap, [1f, 0.5f, 1f, 0.5f], 2);

        var opaque = bitmap.GetPixel(0, 0);
        var translucent = bitmap.GetPixel(1, 0);
        Assert.Equal(SKColors.Red, opaque);
        Assert.Equal(SKColors.Blue.Red, translucent.Red);
        Assert.Equal(SKColors.Blue.Green, translucent.Green);
        Assert.Equal(SKColors.Blue.Blue, translucent.Blue);
        Assert.InRange(translucent.Alpha, 127, 128);
    }

    [Fact]
    public void MaskNormalizationSpansFullOpacityRange()
    {
        var values = new[] { -4f, 0f, 4f };

        BackgroundRemovalService.NormalizeMaskInPlace(values);

        Assert.Equal(0f, values[0]);
        Assert.InRange(values[1], 0.49f, 0.51f);
        Assert.Equal(1f, values[2]);
    }

    [Fact]
    public void OnnxTensorAcceptsAnOversizedPooledBuffer()
    {
        using var value = OrtValue.CreateTensorValueFromMemory<float>(new float[8], [1, 3]);
        Assert.NotNull(value);
    }

    [Fact]
    public void SixCpuAutoProfileUsesOneSixThreadWorker()
    {
        var runtime = RemovalRuntimeSettings.Resolve(new RemovalOptions(), 6);

        Assert.Equal(1, runtime.WorkerCount);
        Assert.Equal(6, runtime.IntraOpThreads);
        Assert.Equal(2, runtime.QueueCapacity);
    }

    [Fact]
    public void ImageDimensionsAreValidatedBeforeEnqueueing()
    {
        var validator = new ImageInputValidator(Options.Create(new RemovalOptions
        {
            MaxImageWidth = 0,
            MaxImageHeight = 10,
            MaxImagePixels = 100
        }));

        var exception = Assert.Throws<ApiException>(() =>
            validator.Validate(SamplePng(), "image/png"));

        Assert.Equal("image_dimensions_too_large", exception.Code);
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
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var accepted = await response.Content.ReadFromJsonAsync<JobAcceptedDto>();
        var job = await WaitForJobAsync(client, accepted!.JobId);
        Assert.Equal("success", job.Status);
        Assert.Equal(1, job.AttemptCount);
        Assert.True(job.PeakRamBytes > 0);
        var result = await client.GetAsync(job.ResultUrl);
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Equal("image/png", result.Content.Headers.ContentType?.MediaType);

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
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var accepted = await response.Content.ReadFromJsonAsync<JobAcceptedDto>();
        var job = await WaitForJobAsync(client, accepted!.JobId);
        Assert.Equal("failed", job.Status);
        Assert.Equal(3, job.AttemptCount);

        var balance = await client.GetFromJsonAsync<BalanceDto>("/v1/balance");
        Assert.Equal(1000.00m, balance!.BalanceRub);
        var usage = await client.GetFromJsonAsync<UsageDto>("/v1/usage");
        Assert.Equal(0, usage!.Successful);
        Assert.Equal(0m, usage.ChargedRub);
    }

    [Fact]
    public async Task RequestWithMoreThanOneImageIsRejected()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        var account = await RegisterAsync(client, "two-images@example.ru");
        client.DefaultRequestHeaders.Add("X-API-Key", account.ApiKey);

        using var form = ImageForm();
        var second = new ByteArrayContent(SamplePng());
        second.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(second, "image", "second.png");
        var response = await client.PostAsync("/v1/remove-background", form);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("single_image_required", error.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task RequestBeyondConfiguredQueueCapacityIsRejected()
    {
        var removal = new BlockingRemovalService();
        using var factory = new ApiFactory(removalService: removal, queueCapacity: 1);
        using var client = factory.CreateClient();
        var account = await RegisterAsync(client, "concurrency@example.ru");
        client.DefaultRequestHeaders.Add("X-API-Key", account.ApiKey);

        using var firstForm = ImageForm();
        var firstRequest = await client.PostAsync("/v1/remove-background", firstForm);
        Assert.Equal(HttpStatusCode.Accepted, firstRequest.StatusCode);
        await removal.Started.WaitAsync(TimeSpan.FromSeconds(5));

        using var secondForm = ImageForm();
        var queued = await client.PostAsync("/v1/remove-background", secondForm);
        Assert.Equal(HttpStatusCode.Accepted, queued.StatusCode);
        using var thirdForm = ImageForm();
        var rejected = await client.PostAsync("/v1/remove-background", thirdForm);
        removal.Release();

        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Equal("5", rejected.Headers.RetryAfter?.Delta?.TotalSeconds.ToString("0")
            ?? rejected.Headers.GetValues("Retry-After").Single());
        var error = await rejected.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("server_busy", error.GetProperty("error").GetProperty("code").GetString());

        var first = await firstRequest.Content.ReadFromJsonAsync<JobAcceptedDto>();
        var second = await queued.Content.ReadFromJsonAsync<JobAcceptedDto>();
        Assert.Equal("success", (await WaitForJobAsync(client, first!.JobId)).Status);
        Assert.Equal("success", (await WaitForJobAsync(client, second!.JobId)).Status);
    }

    [Fact]
    public async Task ConfiguredWorkersProcessSeveralRequestsAtTheSameTime()
    {
        var removal = new BlockingRemovalService(expectedStarts: 3);
        using var factory = new ApiFactory(removalService: removal, workerCount: 3);
        using var client = factory.CreateClient();
        var account = await RegisterAsync(client, "parallel@example.ru");
        client.DefaultRequestHeaders.Add("X-API-Key", account.ApiKey);

        using var firstForm = ImageForm();
        using var secondForm = ImageForm();
        using var thirdForm = ImageForm();
        var requests = new[]
        {
            client.PostAsync("/v1/remove-background", firstForm),
            client.PostAsync("/v1/remove-background", secondForm),
            client.PostAsync("/v1/remove-background", thirdForm)
        };
        await removal.Started.WaitAsync(TimeSpan.FromSeconds(5));

        removal.Release();
        var completed = await Task.WhenAll(requests);
        Assert.All(completed, response => Assert.Equal(HttpStatusCode.Accepted, response.StatusCode));
        foreach (var response in completed)
        {
            var accepted = await response.Content.ReadFromJsonAsync<JobAcceptedDto>();
            Assert.Equal("success", (await WaitForJobAsync(client, accepted!.JobId)).Status);
        }
    }

    [Fact]
    public async Task TransientWorkerFailureIsRetriedAndRecorded()
    {
        var removal = new FlakyRemovalService(failuresBeforeSuccess: 2);
        using var factory = new ApiFactory(removalService: removal, maxAttempts: 3);
        using var client = factory.CreateClient();
        var account = await RegisterAsync(client, "retry@example.ru");
        client.DefaultRequestHeaders.Add("X-API-Key", account.ApiKey);

        using var form = ImageForm();
        var response = await client.PostAsync("/v1/remove-background", form);
        var accepted = await response.Content.ReadFromJsonAsync<JobAcceptedDto>();
        var job = await WaitForJobAsync(client, accepted!.JobId);

        Assert.Equal("success", job.Status);
        Assert.Equal(3, job.AttemptCount);
        Assert.Equal(3, removal.Calls);
        var metrics = await client.GetFromJsonAsync<MetricsDto>("/v1/metrics");
        Assert.Equal(1, metrics!.Successful);
        Assert.Equal(1, metrics.Total);
        Assert.NotNull(metrics.ProcessingP50Ms);
    }

    [Fact]
    public async Task ProcessingTimeoutIsRetriedOnce()
    {
        var removal = new TimeoutRemovalService();
        using var factory = new ApiFactory(
            removalService: removal,
            maxAttempts: 2,
            processingTimeoutSeconds: 1);
        using var client = factory.CreateClient();
        var account = await RegisterAsync(client, "timeout@example.ru");
        client.DefaultRequestHeaders.Add("X-API-Key", account.ApiKey);

        using var form = ImageForm();
        var response = await client.PostAsync("/v1/remove-background", form);
        var accepted = await response.Content.ReadFromJsonAsync<JobAcceptedDto>();
        var job = await WaitForJobAsync(client, accepted!.JobId, attempts: 160);

        Assert.Equal("failed", job.Status);
        Assert.Equal(2, job.AttemptCount);
        Assert.Equal(2, removal.Calls);
    }

    [Fact]
    public async Task DeclaredFormatMustMatchActualImage()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        var account = await RegisterAsync(client, "format@example.ru");
        client.DefaultRequestHeaders.Add("X-API-Key", account.ApiKey);

        using var content = new ByteArrayContent(SamplePng());
        content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        using var form = new MultipartFormDataContent();
        form.Add(content, "image", "fake.jpg");
        var response = await client.PostAsync("/v1/remove-background", form);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("content_type_mismatch", error.GetProperty("error").GetProperty("code").GetString());
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
            .GetProperty("post").GetProperty("responses").TryGetProperty("202", out _));
        Assert.True(document.GetProperty("paths").GetProperty("/v1/jobs/{jobId}/result")
            .GetProperty("get").GetProperty("responses").GetProperty("200")
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
    private sealed record JobAcceptedDto(Guid JobId);
    private sealed record JobDto(
        Guid JobId,
        string Status,
        int AttemptCount,
        long PeakRamBytes,
        string? ResultUrl);
    private sealed record MetricsDto(int Total, int Successful, long? ProcessingP50Ms);

    private static async Task<JobDto> WaitForJobAsync(HttpClient client, Guid jobId, int attempts = 100)
    {
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            var job = await client.GetFromJsonAsync<JobDto>($"/v1/jobs/{jobId}");
            if (job!.Status is "success" or "failed")
            {
                return job;
            }
            await Task.Delay(25);
        }
        throw new TimeoutException($"Job {jobId} did not finish");
    }
}
