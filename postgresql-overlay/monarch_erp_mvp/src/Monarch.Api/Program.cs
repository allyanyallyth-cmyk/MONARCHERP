using Monarch.Core.Models;
using Monarch.Core.Services;
using Monarch.PostgreSql.Infrastructure;
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy.WithOrigins("http://localhost", "https://localhost").AllowAnyHeader().AllowAnyMethod()));
var postgresConnectionString = Environment.GetEnvironmentVariable(PostgreSqlOptions.ConnectionStringEnvironmentVariable);
if (!string.IsNullOrWhiteSpace(postgresConnectionString))
{
    var factory = new PostgreSqlConnectionFactory(new PostgreSqlOptions(postgresConnectionString));
    builder.Services.AddSingleton(factory);
    builder.Services.AddSingleton<PostgreSqlSchemaMigrator>();
    builder.Services.AddSingleton<PostgreSqlHealthProbe>();
}
var app = builder.Build();
if (!string.IsNullOrWhiteSpace(postgresConnectionString) &&
    string.Equals(Environment.GetEnvironmentVariable("MONARCH_POSTGRESQL_AUTO_MIGRATE"), "true", StringComparison.OrdinalIgnoreCase))
    await app.Services.GetRequiredService<PostgreSqlSchemaMigrator>().ApplyAsync();
app.UseCors();
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