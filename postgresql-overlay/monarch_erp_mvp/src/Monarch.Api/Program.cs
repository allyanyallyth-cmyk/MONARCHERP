using Monarch.Core.Abstractions;
using Monarch.Core.Models;
using Monarch.Core.Services;
using Monarch.PostgreSql.Infrastructure;
using Monarch.Api.Services;
using Monarch.Api.Endpoints;
using Monarch.Api.Security;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("identity-enrollment", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 12,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
});
var jwtAuthenticationConfigured = JwtAuthenticationRegistration.AddFromConfiguration(builder.Services, builder.Configuration);
var oidcAuthorityConfigured = !string.IsNullOrWhiteSpace(builder.Configuration[JwtAuthenticationRegistration.OidcAuthorityEnvironmentVariable])
    && !string.IsNullOrWhiteSpace(builder.Configuration[JwtAuthenticationRegistration.OidcAudienceEnvironmentVariable]);
var enrollmentClientId = builder.Configuration[OidcEnrollmentProofValidator.ClientIdEnvironmentVariable];
var identityEnrollmentConfigured = oidcAuthorityConfigured && !string.IsNullOrWhiteSpace(enrollmentClientId);
if (!string.IsNullOrWhiteSpace(enrollmentClientId) && !oidcAuthorityConfigured)
    throw new InvalidOperationException("OIDC identity enrollment requires MONARCH_OIDC_AUTHORITY and MONARCH_OIDC_AUDIENCE.");
builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy.WithOrigins("http://localhost", "https://localhost").AllowAnyHeader().AllowAnyMethod()));
var postgresConnectionString = Environment.GetEnvironmentVariable(PostgreSqlOptions.ConnectionStringEnvironmentVariable);
if (jwtAuthenticationConfigured && string.IsNullOrWhiteSpace(postgresConnectionString))
    throw new InvalidOperationException("JWT-protected API mode requires a configured PostgreSQL connection.");

if (!string.IsNullOrWhiteSpace(postgresConnectionString))
{
    var factory = new PostgreSqlConnectionFactory(new PostgreSqlOptions(postgresConnectionString));
    builder.Services.AddSingleton(factory);
    builder.Services.AddSingleton<PostgreSqlSchemaMigrator>();
    builder.Services.AddSingleton<PostgreSqlHealthProbe>();
    builder.Services.AddScoped<IGeneralLedgerStore, PostgreSqlGeneralLedgerStore>();
    builder.Services.AddScoped<GeneralLedgerApplicationService>();
    if (identityEnrollmentConfigured)
    {
        builder.Services.AddSingleton<OidcEnrollmentProofValidator>();
        builder.Services.AddScoped<IdentityEnrollmentApplicationService>();
    }
}
var app = builder.Build();
if (!string.IsNullOrWhiteSpace(postgresConnectionString) &&
    string.Equals(Environment.GetEnvironmentVariable("MONARCH_POSTGRESQL_AUTO_MIGRATE"), "true", StringComparison.OrdinalIgnoreCase))
    await app.Services.GetRequiredService<PostgreSqlSchemaMigrator>().ApplyAsync();
app.UseCors();
app.UseRateLimiter();
if (jwtAuthenticationConfigured)
{
    app.UseAuthentication();
    app.UseAuthorization();
}
if (jwtAuthenticationConfigured && !string.IsNullOrWhiteSpace(postgresConnectionString))
{
    app.MapGeneralLedgerEndpoints();
    if (identityEnrollmentConfigured)
        app.MapIdentityEnrollmentEndpoints();
}
app.MapGet("/health/postgresql", async (IServiceProvider services, CancellationToken cancellationToken) =>
{
    var probe = services.GetService<PostgreSqlHealthProbe>();
    if (probe is null) return Results.Ok(new { configured = false, status = "offline-mode" });
    var available = await probe.IsAvailableAsync(cancellationToken);
    return available ? Results.Ok(new { configured = true, status = "available" })
        : Results.Json(new { configured = true, status = "unavailable" }, statusCode: 503);
});
app.MapGet("/health", () => Results.Ok(new { status = "ok", product = "MONARCH ERP", stage = "foundation-mvp" }));
app.MapGet("/api/dashboard/demo", () =>
{
    var today = DateOnly.FromDateTime(DateTime.Today);
    var inventory = new[] {
        new InventoryItem(Guid.NewGuid(), "OIL-530", "زيت محركات 5W-30", "عبوة", 8, 15, 30),
        new InventoryItem(Guid.NewGuid(), "FLT-01", "فلتر زيت", "قطعة", 42, 10, 20)
    };
    var invoices = new[] { new SalesInvoice(Guid.NewGuid(), today, "DEMO-001", 2500m, 2000m) };
    var movements = new[] {
        new CashMovement(Guid.NewGuid(), today, CashMovementType.Receipt, PaymentMethod.Cash, 1200m, "قبض تجريبي"),
        new CashMovement(Guid.NewGuid(), today, CashMovementType.Payment, PaymentMethod.Cash, 300m, "صرف تجريبي")
    };
    var snapshot = DashboardCalculator.Calculate(today, invoices, movements, inventory);
    var alerts = AlertEngine.Evaluate(inventory, snapshot, DateTimeOffset.UtcNow);
    return Results.Ok(new { demo = true, note = "بيانات تجريبية وليست أرصدة فعلية", dashboard = snapshot, alerts });
});
app.Run();