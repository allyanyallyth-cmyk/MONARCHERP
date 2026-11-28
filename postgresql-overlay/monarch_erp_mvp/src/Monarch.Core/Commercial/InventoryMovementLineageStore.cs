using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Monarch.Core.Commercial;

/// <summary>
/// Read-only, tenant/branch-scoped audit view connecting immutable stock movements to
/// commercial documents/returns, cost allocations, accounting outbox events and deterministic
/// tenant-ledger journals. It reports missing links; it never repairs or posts data.
/// </summary>
public sealed class InventoryMovementLineageStore
{
    private readonly string _connectionString;

    public InventoryMovementLineageStore(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath)) throw new ArgumentException("Database path is required.", nameof(databasePath));
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(databasePath), ForeignKeys = true
        }.ToString();
    }

    public InventoryMovementLineageResult Inspect(Guid userId, Guid companyId, Guid branchId, DateTimeOffset asOfUtc)
    {
        if (userId == Guid.Empty || companyId == Guid.Empty || branchId == Guid.Empty)
            throw new ArgumentException("User, company and branch identifiers are required.");
        if (asOfUtc == default) throw new ArgumentOutOfRangeException(nameof(asOfUtc));
        var cutoff = asOfUtc.ToUniversalTime();
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        RequireBranchAccess(connection, transaction, userId, companyId, branchId);

        var rows = new List<InventoryMovementLineageEntry>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = @"SELECT
 m.id,m.occurred_utc,m.ledger_sequence,m.movement_type,m.quantity_milli,m.unit_cost_minor,
 m.warehouse_id,w.code,m.item_id,i.sku,i.name,m.source_type,m.source_id,
 COALESCE(a.document_id,d.id,rd.id),COALESCE(d.document_number,rd.return_number),
 COALESCE(d.document_type,rd.return_type),rd.source_document_id,source_doc.document_number,
 COALESCE(a.id,ra.id),COALESCE(a.allocation_type,ra.allocation_type),COALESCE(a.total_cost_minor,ra.total_cost_minor),
 COALESCE(o.status,ro.status),COALESCE(o.event_type,ro.event_type),
 COALESCE(j.id,rj.id),COALESCE(j.number,rj.number),COALESCE(j.status,rj.status),
 CASE WHEN m.source_type='WarehouseTransfer' THEN
   (SELECT CASE WHEN COUNT(*)=2 AND COUNT(DISTINCT peer.movement_type)=2
      AND COUNT(DISTINCT peer.warehouse_id)=2
      AND MIN(peer.quantity_milli)=MAX(peer.quantity_milli)
      AND MIN(peer.unit_cost_minor)=MAX(peer.unit_cost_minor) THEN 2 ELSE 0 END
    FROM monarch_stock_movements peer WHERE peer.company_id=m.company_id
      AND peer.source_type='WarehouseTransfer' AND peer.source_id=m.source_id
      AND peer.item_id=m.item_id AND peer.movement_type IN('TransferIn','TransferOut'))
   ELSE 0 END
FROM monarch_stock_movements m
JOIN monarch_warehouses w ON w.company_id=m.company_id AND w.id=m.warehouse_id AND w.branch_id=m.branch_id
JOIN monarch_items i ON i.company_id=m.company_id AND i.id=m.item_id
LEFT JOIN monarch_inventory_cost_allocations a ON a.company_id=m.company_id AND a.movement_id=m.id
LEFT JOIN monarch_commercial_documents d ON d.company_id=m.company_id AND d.id=COALESCE(a.document_id,
 CASE WHEN m.source_type='CommercialDocument' THEN (SELECT x.id FROM monarch_commercial_documents x
   WHERE x.company_id=m.company_id AND lower(replace(x.id,'-',''))=lower(substr(m.source_id,1,32)) LIMIT 1) END)
LEFT JOIN monarch_accounting_posting_outbox o ON o.company_id=d.company_id AND o.document_id=d.id
LEFT JOIN monarch_commercial_returns r ON r.company_id=m.company_id AND r.id=CASE WHEN m.source_type='CommercialReturn'
   THEN (SELECT x.id FROM monarch_commercial_returns x WHERE x.company_id=m.company_id
      AND lower(replace(x.id,'-',''))=lower(substr(m.source_id,1,32)) LIMIT 1) END
LEFT JOIN monarch_inventory_return_cost_allocations ra ON ra.company_id=m.company_id AND ra.movement_id=m.id
LEFT JOIN monarch_commercial_returns rd ON rd.company_id=m.company_id AND rd.id=COALESCE(ra.return_id,r.id)
LEFT JOIN monarch_commercial_documents source_doc ON source_doc.company_id=rd.company_id AND source_doc.id=rd.source_document_id
LEFT JOIN monarch_return_accounting_outbox ro ON ro.company_id=rd.company_id AND ro.return_id=rd.id
LEFT JOIN monarch_tenant_journals j ON j.company_id=m.company_id AND j.branch_id=m.branch_id
 AND j.number=('AUTO-COM-'||upper(replace(d.id,'-','')))
LEFT JOIN monarch_tenant_journals rj ON rj.company_id=m.company_id AND rj.branch_id=m.branch_id
 AND rj.number=('AUTO-RET-'||upper(replace(rd.id,'-','')))
WHERE m.company_id=$company AND m.branch_id=$branch AND m.occurred_utc <= $cutoff
ORDER BY m.occurred_utc,m.ledger_sequence,m.id;";
            command.Parameters.AddWithValue("$company", companyId.ToString());
            command.Parameters.AddWithValue("$branch", branchId.ToString());
            command.Parameters.AddWithValue("$cutoff", cutoff.ToString("O", CultureInfo.InvariantCulture));
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var movementId = Guid.Parse(reader.GetString(0));
                var movementType = reader.GetString(3);
                var sourceType = reader.IsDBNull(11) ? null : reader.GetString(11);
                var allocationType = reader.IsDBNull(19) ? null : reader.GetString(19);
                var outboxStatus = reader.IsDBNull(21) ? null : reader.GetString(21);
                var journalStatus = reader.IsDBNull(25) ? null : reader.GetString(25);
                var transferPairCount = reader.GetInt64(26);
                var state = DetermineState(sourceType, movementType, allocationType,
                    reader.IsDBNull(13) ? null : reader.GetString(13), outboxStatus,
                    journalStatus, transferPairCount);
                rows.Add(new InventoryMovementLineageEntry(movementId,
                    DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture), reader.GetInt64(2),
                    movementType, reader.GetInt64(4), reader.GetInt64(5), Guid.Parse(reader.GetString(6)),
                    reader.GetString(7), Guid.Parse(reader.GetString(8)), reader.GetString(9), reader.GetString(10),
                    sourceType, reader.IsDBNull(12) ? null : reader.GetString(12),
                    reader.IsDBNull(13) ? null : reader.GetString(13),
                    reader.IsDBNull(14) ? null : reader.GetString(14),
                    reader.IsDBNull(15) ? null : reader.GetString(15),
                    reader.IsDBNull(16) ? null : reader.GetString(16),
                    reader.IsDBNull(17) ? null : reader.GetString(17),
                    reader.IsDBNull(18) ? null : reader.GetString(18), allocationType,
                    reader.IsDBNull(20) ? null : reader.GetInt64(20), outboxStatus,
                    reader.IsDBNull(22) ? null : reader.GetString(22),
                    reader.IsDBNull(23) ? null : reader.GetString(23),
                    reader.IsDBNull(24) ? null : reader.GetString(24), journalStatus, state));
            }
        }
        transaction.Commit();
        var entries = rows.AsReadOnly();
        return new InventoryMovementLineageResult(companyId, branchId, cutoff, DateTimeOffset.UtcNow,
            entries.Count, entries.Count(x => x.State == InventoryLineageState.Linked),
            entries.Count(x => x.State != InventoryLineageState.Linked && x.State != InventoryLineageState.SourceNotRequired
                && x.State != InventoryLineageState.TransferPaired), entries);
    }

    private static InventoryLineageState DetermineState(string? sourceType, string movementType,
        string? allocationType, string? sourceDocumentId, string? outboxStatus,
        string? journalStatus, long transferPairCount)
    {
        if (sourceType == "WarehouseTransfer")
            return transferPairCount == 2 ? InventoryLineageState.TransferPaired : InventoryLineageState.TransferPairIncomplete;
        if (sourceType == "CommercialDocument")
        {
            if (allocationType is null) return InventoryLineageState.MissingCostAllocation;
            if (sourceDocumentId is null) return InventoryLineageState.MissingSourceDocument;
            if (outboxStatus is null) return InventoryLineageState.MissingAccountingOutbox;
            if (outboxStatus == "Failed") return InventoryLineageState.OutboxFailed;
            if (outboxStatus != "Processed") return InventoryLineageState.OutboxPending;
            if (journalStatus is null) return InventoryLineageState.MissingJournal;
            return journalStatus is "Posted" or "Reversed" ? InventoryLineageState.Linked : InventoryLineageState.JournalNotPosted;
        }
        if (sourceType == "CommercialReturn")
        {
            if (allocationType is null) return InventoryLineageState.MissingReturnCostAllocation;
            if (sourceDocumentId is null) return InventoryLineageState.MissingSourceDocument;
            if (outboxStatus is null) return InventoryLineageState.MissingAccountingOutbox;
            if (outboxStatus == "Failed") return InventoryLineageState.OutboxFailed;
            if (outboxStatus != "Processed") return InventoryLineageState.OutboxPending;
            if (journalStatus is null) return InventoryLineageState.MissingJournal;
            return journalStatus is "Posted" or "Reversed" ? InventoryLineageState.Linked : InventoryLineageState.JournalNotPosted;
        }
        return movementType == "Opening" || sourceType is null
            ? InventoryLineageState.SourceNotRequired : InventoryLineageState.UnclassifiedSource;
    }

    private static void RequireBranchAccess(SqliteConnection connection, SqliteTransaction transaction,
        Guid userId, Guid companyId, Guid branchId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"SELECT 1 FROM monarch_users u
JOIN monarch_user_company_access ca ON ca.user_id=u.id
JOIN monarch_user_branch_access ba ON ba.user_id=ca.user_id AND ba.company_id=ca.company_id
JOIN monarch_companies co ON co.id=ca.company_id
JOIN monarch_branches br ON br.company_id=ba.company_id AND br.id=ba.branch_id
WHERE u.id=$user AND u.is_active=1 AND ca.company_id=$company AND ca.is_active=1
AND ba.branch_id=$branch AND ba.is_active=1 AND co.is_active=1 AND br.is_active=1
AND EXISTS (SELECT 1 FROM monarch_user_roles ur
  JOIN monarch_role_permissions rp ON rp.role_id=ur.role_id
  WHERE ur.user_id=u.id AND rp.permission_code='reports.read');";
        command.Parameters.AddWithValue("$user", userId.ToString());
        command.Parameters.AddWithValue("$company", companyId.ToString());
        command.Parameters.AddWithValue("$branch", branchId.ToString());
        if (command.ExecuteScalar() is null) throw new UnauthorizedAccessException("No active access to the selected company and branch.");
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand(); command.CommandText = "PRAGMA foreign_keys=ON;"; command.ExecuteNonQuery();
        return connection;
    }
}

public enum InventoryLineageState
{
    Linked, SourceNotRequired, TransferPaired, TransferPairIncomplete, MissingCostAllocation,
    MissingReturnCostAllocation, MissingSourceDocument, MissingAccountingOutbox, OutboxPending,
    OutboxFailed, MissingJournal, JournalNotPosted, UnclassifiedSource
}

public sealed record InventoryMovementLineageEntry(Guid MovementId, DateTimeOffset OccurredUtc,
    long LedgerSequence, string MovementType, long QuantityMilli, long UnitCostMinor,
    Guid WarehouseId, string WarehouseCode, Guid ItemId, string Sku, string ItemName,
    string? SourceType, string? SourceId, string? SourceRecordId, string? SourceRecordNumber,
    string? SourceRecordType, string? OriginalDocumentId, string? OriginalDocumentNumber,
    string? CostAllocationId, string? AllocationType, long? AllocatedCostMinor,
    string? OutboxStatus, string? OutboxEventType, string? JournalId, string? JournalNumber,
    string? JournalStatus, InventoryLineageState State);

public sealed record InventoryMovementLineageResult(Guid CompanyId, Guid BranchId, DateTimeOffset AsOfUtc,
    DateTimeOffset InspectedUtc, int MovementCount, int FullyLinkedCount, int ExceptionCount,
    IReadOnlyList<InventoryMovementLineageEntry> Entries);
