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

builder.Services.AddDbContext<AppDbContext>((services, options) =>
{
    var configuration = services.GetRequiredService<IConfiguration>();
    var connectionString = configuration[$"{DatabaseOptions.SectionName}:ConnectionString"]
        ?? throw new InvalidOperationException("Database:ConnectionString is required");
    options.UseMySQL(connectionString, mysql => mysql.EnableRetryOnFailure(5));
});
builder.Services.AddScoped<BillingService>();
builder.Services.AddSingleton<IBackgroundRemovalService, BackgroundRemovalService>();
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
        Description = "CPU-only удаление фона, рублёвый баланс и API-ключи"
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
app.MapGet("/health/ready", async (AppDbContext db, CancellationToken cancellationToken) =>
    {
        await db.Database.ExecuteSqlRawAsync("SELECT 1", cancellationToken);
        return Results.Ok(new { status = "ready", model = "birefnet-general-lite", device = "cpu" });
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
    if (app.Environment.IsEnvironment("Testing"))
    {
        await db.Database.EnsureCreatedAsync();
        return;
    }

    await db.Database.MigrateAsync();
}

public partial class Program;
