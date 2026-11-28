using System;
using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;

namespace Monarch.Core.Autonomy;

/// <summary>Durable, tenant-scoped and append-only Green Ledger. Migrations are applied explicitly by deployment tooling.</summary>
public sealed class PersistentGreenComplianceLedger
{
    private readonly string _connectionString;
    public PersistentGreenComplianceLedger(string databasePath)
    {
        var full = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = full, ForeignKeys = true }.ToString();
    }

    public Guid AddCarbonFactorDraft(Guid actor, Guid company, string activity, string unit, decimal kgPerUnit,
        string source, string version, DateTimeOffset effectiveFrom, DateTimeOffset? effectiveTo)
    {
        if (actor == Guid.Empty || company == Guid.Empty || string.IsNullOrWhiteSpace(activity) || string.IsNullOrWhiteSpace(unit) ||
            string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(version) || kgPerUnit < 0 ||
            (effectiveTo.HasValue && effectiveTo <= effectiveFrom)) throw new ArgumentException("Invalid carbon factor version.");
        var id = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        using var c = Open(); using var tx = c.BeginTransaction();
        using var q = c.CreateCommand(); q.Transaction = tx;
        q.CommandText = @"INSERT INTO monarch_carbon_factor_versions
(id,company_id,activity_code,unit_code,kg_co2e_per_unit,source_reference,version_code,effective_from_utc,effective_to_utc,approval_state,created_by,created_utc)
VALUES($id,$co,$activity,$unit,$factor,$source,$version,$from,$to,'Draft',$actor,$now);";
        q.Parameters.AddWithValue("$id", id.ToString()); q.Parameters.AddWithValue("$co", company.ToString()); q.Parameters.AddWithValue("$activity", activity.Trim());
        q.Parameters.AddWithValue("$unit", unit.Trim()); q.Parameters.AddWithValue("$factor", kgPerUnit.ToString(CultureInfo.InvariantCulture)); q.Parameters.AddWithValue("$source", source.Trim());
        q.Parameters.AddWithValue("$version", version.Trim()); q.Parameters.AddWithValue("$from", effectiveFrom.ToUniversalTime().ToString("O"));
        q.Parameters.AddWithValue("$to", (object?)(effectiveTo?.ToUniversalTime().ToString("O")) ?? DBNull.Value);
        q.Parameters.AddWithValue("$actor", actor.ToString()); q.Parameters.AddWithValue("$now", now.ToString("O")); q.ExecuteNonQuery();
        Audit(c, tx, actor, company, "CarbonFactorDraftCreated", id, version); tx.Commit(); return id;
    }

    public void ApproveCarbonFactor(Guid actor, Guid company, Guid factorId)
    {
        using var c = Open(); using var tx = c.BeginTransaction(); var now = DateTimeOffset.UtcNow.ToString("O");
        using var q = c.CreateCommand(); q.Transaction = tx;
        q.CommandText = @"UPDATE monarch_carbon_factor_versions SET approval_state='Approved',approved_by=$actor,approved_utc=$now
WHERE id=$id AND company_id=$co AND approval_state='Draft';";
        q.Parameters.AddWithValue("$actor", actor.ToString()); q.Parameters.AddWithValue("$now", now); q.Parameters.AddWithValue("$id", factorId.ToString()); q.Parameters.AddWithValue("$co", company.ToString());
        if (q.ExecuteNonQuery() != 1) throw new InvalidOperationException("Carbon factor draft not found or already decided.");
        Audit(c, tx, actor, company, "CarbonFactorApproved", factorId, null); tx.Commit();
    }

    public CarbonDocumentSnapshot RecordCarbonDocument(Guid actor, Guid company, Guid? branch, string documentType,
        string documentId, string activity, decimal quantity, DateTimeOffset transactionUtc)
    {
        if (actor == Guid.Empty || company == Guid.Empty || quantity < 0 || string.IsNullOrWhiteSpace(documentType) ||
            string.IsNullOrWhiteSpace(documentId) || string.IsNullOrWhiteSpace(activity)) throw new ArgumentException("Invalid carbon document reference.");
        using var c = Open(); using var tx = c.BeginTransaction();
        string factorId, version; decimal factor;
        using (var find = c.CreateCommand())
        {
            find.Transaction = tx;
            find.CommandText = @"SELECT id,version_code,kg_co2e_per_unit FROM monarch_carbon_factor_versions
WHERE company_id=$co AND activity_code=$activity AND approval_state='Approved' AND effective_from_utc<=$at
AND (effective_to_utc IS NULL OR $at<effective_to_utc) ORDER BY effective_from_utc DESC LIMIT 1;";
            find.Parameters.AddWithValue("$co", company.ToString()); find.Parameters.AddWithValue("$activity", activity);
            find.Parameters.AddWithValue("$at", transactionUtc.ToUniversalTime().ToString("O"));
            using var r = find.ExecuteReader(); if (!r.Read()) throw new InvalidOperationException("No approved carbon factor is effective for this transaction date.");
            factorId = r.GetString(0); version = r.GetString(1); factor = decimal.Parse(r.GetString(2), CultureInfo.InvariantCulture);
        }
        var id = Guid.NewGuid(); var kg = quantity * factor; var now = DateTimeOffset.UtcNow.ToString("O");
        using (var insert = c.CreateCommand())
        {
            insert.Transaction = tx;
            insert.CommandText = @"INSERT INTO monarch_carbon_document_snapshots
(id,company_id,branch_id,document_type,document_id,activity_code,quantity,kg_co2e,factor_version_id,transaction_utc,recorded_by,recorded_utc)
VALUES($id,$co,$branch,$type,$doc,$activity,$qty,$kg,$factor,$at,$actor,$now);";
            insert.Parameters.AddWithValue("$id", id.ToString()); insert.Parameters.AddWithValue("$co", company.ToString());
            insert.Parameters.AddWithValue("$branch", (object?)(branch?.ToString()) ?? DBNull.Value); insert.Parameters.AddWithValue("$type", documentType);
            insert.Parameters.AddWithValue("$doc", documentId); insert.Parameters.AddWithValue("$activity", activity);
            insert.Parameters.AddWithValue("$qty", quantity.ToString(CultureInfo.InvariantCulture)); insert.Parameters.AddWithValue("$kg", kg.ToString(CultureInfo.InvariantCulture));
            insert.Parameters.AddWithValue("$factor", factorId); insert.Parameters.AddWithValue("$at", transactionUtc.ToUniversalTime().ToString("O"));
            insert.Parameters.AddWithValue("$actor", actor.ToString()); insert.Parameters.AddWithValue("$now", now); insert.ExecuteNonQuery();
        }
        Audit(c, tx, actor, company, "CarbonDocumentRecorded", id, "factorVersion=" + version + ";document=" + documentType + ":" + documentId);
        tx.Commit(); return new CarbonDocumentSnapshot(activity, quantity, kg, version, transactionUtc, documentType + ":" + documentId);
    }

    private static void Audit(SqliteConnection c, SqliteTransaction tx, Guid actor, Guid company, string action, Guid id, string? details)
    {
        using var a = c.CreateCommand(); a.Transaction = tx;
        a.CommandText = @"INSERT INTO monarch_company_audit_log(company_id,actor_user_id,action,entity_type,entity_id,details,occurred_utc)
VALUES($co,$actor,$action,'GreenLedger',$id,$details,$now);";
        a.Parameters.AddWithValue("$co", company.ToString()); a.Parameters.AddWithValue("$actor", actor.ToString()); a.Parameters.AddWithValue("$action", action);
        a.Parameters.AddWithValue("$id", id.ToString()); a.Parameters.AddWithValue("$details", (object?)details ?? DBNull.Value); a.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O")); a.ExecuteNonQuery();
    }
    private SqliteConnection Open() { var c = new SqliteConnection(_connectionString); c.Open(); using var p = c.CreateCommand(); p.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;"; p.ExecuteNonQuery(); return c; }
}
