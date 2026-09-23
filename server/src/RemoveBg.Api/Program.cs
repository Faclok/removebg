using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;
using RemoveBg.Api.Configuration;
using RemoveBg.Api.Data;
using RemoveBg.Api.Endpoints;
using RemoveBg.Api.Infrastructure;
using RemoveBg.Api.Security;
using RemoveBg.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<DatabaseOptions>(builder.Configuration.GetSection(DatabaseOptions.SectionName));
builder.Services.Configure<AccountOptions>(builder.Configuration.GetSection(AccountOptions.SectionName));
builder.Services.Configure<RemovalOptions>(builder.Configuration.GetSection(RemovalOptions.SectionName));
builder.Services.Configure<PricingOptions>(builder.Configuration.GetSection(PricingOptions.SectionName));
builder.Services.Configure<AdminOptions>(builder.Configuration.GetSection(AdminOptions.SectionName));

var removalConfiguration = builder.Configuration
    .GetSection(RemovalOptions.SectionName)
    .Get<RemovalOptions>() ?? new RemovalOptions();
var removalRuntime = RemovalRuntimeSettings.Resolve(
    removalConfiguration,
    Environment.ProcessorCount);
builder.Services.AddSingleton(removalRuntime);
builder.Services.AddSingleton<ImageProcessingGate>();

builder.Services.AddDbContext<AppDbContext>((services, options) =>
{
    var configuration = services.GetRequiredService<IConfiguration>();
    var provider = configuration[$"{DatabaseOptions.SectionName}:Provider"] ?? "MySql";
    var connectionString = configuration[$"{DatabaseOptions.SectionName}:ConnectionString"]
        ?? throw new InvalidOperationException("Database:ConnectionString is required");
    if (provider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase))
    {
        options.UseSqlite(connectionString);
    }
    else
    {
        options.UseMySQL(connectionString, mysql => mysql.EnableRetryOnFailure(5));
    }
});
builder.Services.AddScoped<BillingService>();
builder.Services.AddSingleton<IBackgroundRemovalService, BackgroundRemovalService>();
builder.Services.AddHostedService<BackgroundRemovalWarmupService>();
builder.Services.AddHttpClient("model-download", client =>
{
    client.Timeout = TimeSpan.FromMinutes(10);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Udalifon-API/1.0");
});
builder.Services
    .AddAuthentication(ApiKeyAuthenticationHandler.AuthenticationSchemeName)
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
        ApiKeyAuthenticationHandler.AuthenticationSchemeName,
        _ => { });
builder.Services.AddAuthorization();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Удалифон API",
        Version = "v1",
        Description = "Удаление фона через ONNX Runtime, рублёвый баланс и API-ключи"
    });
    options.AddSecurityDefinition("ApiKeyAuth", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.ApiKey,
        In = ParameterLocation.Header,
        Name = "X-API-Key",
        Description = "API-ключ вида rb_live_..."
    });
    options.AddSecurityRequirement(document => new OpenApiSecurityRequirement
    {
        [new OpenApiSecuritySchemeReference("ApiKeyAuth", document)] = []
    });
    options.OperationFilter<AllowAnonymousOperationFilter>();
});

var app = builder.Build();

app.UseMiddleware<ApiExceptionMiddleware>();
app.Use(async (context, next) =>
{
    var requestId = Guid.NewGuid();
    context.Items[RequestContext.RequestIdItem] = requestId;
    context.Response.Headers["X-Request-ID"] = requestId.ToString();
    await next(context);
});
app.UseSwagger();
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/swagger/v1/swagger.json", "Удалифон API v1");
    options.RoutePrefix = "docs";
});
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health/live", () => Results.Ok(new { status = "ok" }))
    .WithTags("Health")
    .AllowAnonymous();
app.MapGet("/health/ready", async (
        AppDbContext db,
        IBackgroundRemovalService removal,
        ImageProcessingGate gate,
        RemovalRuntimeSettings runtime,
        CancellationToken cancellationToken) =>
    {
        await db.Database.ExecuteSqlRawAsync("SELECT 1", cancellationToken);
        if (!removal.IsReady)
        {
            return Results.Json(
                new
                {
                    status = "warming_up",
                    database = "ready",
                    model = "loading",
                    device = removal.Device,
                    workers = runtime.WorkerCount,
                    active = gate.ActiveCount,
                    queued = gate.QueuedCount
                },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        return Results.Ok(new
        {
            status = "ready",
            model = "birefnet-general-lite",
            device = removal.Device,
            workers = runtime.WorkerCount,
            intraOpThreads = runtime.IntraOpThreads,
            queueCapacity = runtime.QueueCapacity,
            active = gate.ActiveCount,
            queued = gate.QueuedCount
        });
    })
    .WithTags("Health")
    .AllowAnonymous();
app.MapApiEndpoints();

await InitializeDatabaseAsync(app);
await app.RunAsync();

static async Task InitializeDatabaseAsync(WebApplication app)
{
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var databaseOptions = app.Configuration
        .GetSection(DatabaseOptions.SectionName)
        .Get<DatabaseOptions>() ?? new DatabaseOptions();
    if (app.Environment.IsEnvironment("Testing")
        || databaseOptions.Provider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase))
    {
        await db.Database.EnsureCreatedAsync();
        return;
    }

    await db.Database.MigrateAsync();
}

public partial class Program;
