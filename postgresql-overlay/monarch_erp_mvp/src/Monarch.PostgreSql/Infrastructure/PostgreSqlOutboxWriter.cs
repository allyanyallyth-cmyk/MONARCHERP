using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
namespace Monarch.PostgreSql.Infrastructure;
public sealed class PostgreSqlOutboxWriter
{
    public async Task<bool> EnqueueAsync(TenantPostgreSqlTransaction scope, Guid eventId, string aggregateType,
        string aggregateId, string eventType, object payload, DateTimeOffset occurredAt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (eventId == Guid.Empty) throw new ArgumentException("Event ID is required.", nameof(eventId));
        if (string.IsNullOrWhiteSpace(aggregateType) || string.IsNullOrWhiteSpace(aggregateId) || string.IsNullOrWhiteSpace(eventType))
            throw new ArgumentException("Aggregate and event descriptors are required.");
        ArgumentNullException.ThrowIfNull(payload);
        const string sql = """
            INSERT INTO monarch_outbox(company_id, branch_id, event_id, aggregate_type, aggregate_id, event_type, payload, occurred_at)
            VALUES (@company, @branch, @event, @aggregate_type, @aggregate_id, @event_type, @payload, @occurred_at)
            ON CONFLICT (company_id, event_id) DO NOTHING
            """;
        await using var command = new NpgsqlCommand(sql, scope.Connection, scope.Transaction);
        command.Parameters.AddWithValue("company", Guid.Parse(await ReadSettingAsync(scope, "monarch.company_id", cancellationToken)));
        command.Parameters.AddWithValue("branch", Guid.Parse(await ReadSettingAsync(scope, "monarch.branch_id", cancellationToken)));
        command.Parameters.AddWithValue("event", eventId);
        command.Parameters.AddWithValue("aggregate_type", aggregateType);
        command.Parameters.AddWithValue("aggregate_id", aggregateId);
        command.Parameters.AddWithValue("event_type", eventType);
        command.Parameters.Add("payload", NpgsqlDbType.Jsonb).Value = JsonSerializer.Serialize(payload);
        command.Parameters.AddWithValue("occurred_at", occurredAt);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }
    private static async Task<string> ReadSettingAsync(TenantPostgreSqlTransaction scope, string name, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT current_setting(@setting, true)", scope.Connection, scope.Transaction);
        command.Parameters.AddWithValue("setting", name);
        return (string?)await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Tenant setting {name} is missing.");
    }
}