using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;

namespace Monarch.Core.Zatca;

/// <summary>Append-only, tenant/branch/EGS scoped invoice chain and submission queue.</summary>
public sealed class ZatcaInvoiceChainStore
{
    private readonly string _connectionString;
    public ZatcaInvoiceChainStore(string databasePath)
    { var full = Path.GetFullPath(databasePath); Directory.CreateDirectory(Path.GetDirectoryName(full)!); _connectionString = new SqliteConnectionStringBuilder { DataSource = full, ForeignKeys = true }.ToString(); }

    public long GetNextSequence(Guid userId, Guid companyId, Guid branchId, Guid egsUnitId)
    {
        using var c = Open(); using var tx = c.BeginTransaction(); Require(c, tx, userId, companyId, branchId, "zatca.integration.manage");
        using var q = c.CreateCommand(); q.Transaction = tx;
        q.CommandText = "SELECT COALESCE(MAX(chain_sequence),0)+1 FROM monarch_zatca_invoice_chain WHERE company_id=$co AND branch_id=$br AND egs_unit_id=$egs;";
        q.Parameters.AddWithValue("$co", companyId.ToString()); q.Parameters.AddWithValue("$br", branchId.ToString()); q.Parameters.AddWithValue("$egs", egsUnitId.ToString());
        var result = Convert.ToInt64(q.ExecuteScalar()); tx.Commit(); return result;
    }

    public string GetLatestHash(Guid userId, Guid companyId, Guid branchId, Guid egsUnitId)
    {
        using var c = Open(); using var tx = c.BeginTransaction(); Require(c, tx, userId, companyId, branchId, "zatca.integration.manage");
        using var q = c.CreateCommand(); q.Transaction = tx;
        q.CommandText = "SELECT invoice_hash_base64 FROM monarch_zatca_invoice_chain WHERE company_id=$co AND branch_id=$br AND egs_unit_id=$egs ORDER BY chain_sequence DESC LIMIT 1;";
        q.Parameters.AddWithValue("$co", companyId.ToString()); q.Parameters.AddWithValue("$br", branchId.ToString()); q.Parameters.AddWithValue("$egs", egsUnitId.ToString());
        var result = q.ExecuteScalar() as string ?? ZatcaInvoiceBuilder.FirstInvoicePreviousHash; tx.Commit(); return result;
    }

    public ZatcaChainRecord Append(Guid userId, ZatcaInvoiceDraft draft, ZatcaInvoiceBuildResult built)
    {
        if (draft.CompanyId == Guid.Empty || draft.BranchId == Guid.Empty || draft.EgsUnitId == Guid.Empty || built.Uuid != draft.Uuid) throw new ArgumentException("Tenant/EGS identity mismatch.");
        using var c = Open(); using var tx = c.BeginTransaction(); Require(c, tx, userId, draft.CompanyId, draft.BranchId, "zatca.integration.manage");
        var previous = ZatcaInvoiceBuilder.FirstInvoicePreviousHash; long sequence = 1;
        using (var read = c.CreateCommand())
        {
            read.Transaction = tx; read.CommandText = "SELECT chain_sequence,invoice_hash_base64 FROM monarch_zatca_invoice_chain WHERE company_id=$co AND branch_id=$br AND egs_unit_id=$egs ORDER BY chain_sequence DESC LIMIT 1;";
            read.Parameters.AddWithValue("$co", draft.CompanyId.ToString()); read.Parameters.AddWithValue("$br", draft.BranchId.ToString()); read.Parameters.AddWithValue("$egs", draft.EgsUnitId.ToString());
            using var r = read.ExecuteReader(); if (r.Read()) { sequence = checked(r.GetInt64(0) + 1); previous = r.GetString(1); }
        }
        if (draft.InvoiceCounterValue != sequence) throw new InvalidOperationException("Invoice counter value does not match the next EGS chain sequence.");
        if (!string.Equals(previous, built.PreviousInvoiceHashBase64, StringComparison.Ordinal)) throw new InvalidOperationException("Invoice chain changed; rebuild the invoice using the latest previous hash.");
        var computedHash = new ZatcaInvoiceHashService().ComputeHashBase64(built.Xml);
        if (!string.Equals(computedHash, built.InvoiceHashBase64, StringComparison.Ordinal)) throw new InvalidOperationException("Invoice XML does not match its supplied SHA-256 digest.");
        if (draft.Kind == ZatcaInvoiceKind.SimplifiedTaxInvoice &&
            (built.Xml.IndexOf("SignatureValue", StringComparison.Ordinal) < 0 ||
             built.Xml.IndexOf("X509Certificate", StringComparison.Ordinal) < 0 ||
             built.Xml.IndexOf("QualifyingProperties", StringComparison.Ordinal) < 0))
            throw new InvalidOperationException("Simplified Phase-2 invoice must contain a complete XAdES signature and signing certificate in UBL XML.");
        var now = DateTimeOffset.UtcNow; var id = Guid.NewGuid();
        using (var insert = c.CreateCommand())
        {
            insert.Transaction = tx; insert.CommandText = @"INSERT INTO monarch_zatca_invoice_chain(id,company_id,branch_id,egs_unit_id,chain_sequence,uuid,invoice_number,invoice_kind,invoice_hash_base64,previous_invoice_hash_base64,invoice_xml,qr_payload_base64,submission_status,created_utc,created_by)
VALUES($id,$co,$br,$egs,$seq,$uuid,$number,$kind,$hash,$previous,$xml,$qr,'Pending',$now,$user);";
            insert.Parameters.AddWithValue("$id", id.ToString()); insert.Parameters.AddWithValue("$co", draft.CompanyId.ToString()); insert.Parameters.AddWithValue("$br", draft.BranchId.ToString()); insert.Parameters.AddWithValue("$egs", draft.EgsUnitId.ToString()); insert.Parameters.AddWithValue("$seq", sequence); insert.Parameters.AddWithValue("$uuid", draft.Uuid.ToString()); insert.Parameters.AddWithValue("$number", draft.InvoiceNumber); insert.Parameters.AddWithValue("$kind", draft.Kind.ToString()); insert.Parameters.AddWithValue("$hash", built.InvoiceHashBase64); insert.Parameters.AddWithValue("$previous", built.PreviousInvoiceHashBase64); insert.Parameters.AddWithValue("$xml", built.Xml); insert.Parameters.AddWithValue("$qr", built.QrPayloadBase64); insert.Parameters.AddWithValue("$now", now.ToString("O")); insert.Parameters.AddWithValue("$user", userId.ToString()); insert.ExecuteNonQuery();
        }
        using (var audit = c.CreateCommand()) { audit.Transaction = tx; audit.CommandText = "INSERT INTO monarch_company_audit_log(company_id,branch_id,actor_user_id,action,entity_type,entity_id,occurred_utc) VALUES($co,$br,$user,'ZatcaInvoicePrepared','ZatcaInvoice',$id,$now);"; audit.Parameters.AddWithValue("$co", draft.CompanyId.ToString()); audit.Parameters.AddWithValue("$br", draft.BranchId.ToString()); audit.Parameters.AddWithValue("$user", userId.ToString()); audit.Parameters.AddWithValue("$id", id.ToString()); audit.Parameters.AddWithValue("$now", now.ToString("O")); audit.ExecuteNonQuery(); }
        using (var queue = c.CreateCommand()) { queue.Transaction = tx; queue.CommandText = "INSERT INTO monarch_zatca_submission_outbox(id,company_id,branch_id,chain_record_id,submission_status,created_utc) VALUES($id,$co,$br,$record,'Pending',$now);"; queue.Parameters.AddWithValue("$id", Guid.NewGuid().ToString()); queue.Parameters.AddWithValue("$co", draft.CompanyId.ToString()); queue.Parameters.AddWithValue("$br", draft.BranchId.ToString()); queue.Parameters.AddWithValue("$record", id.ToString()); queue.Parameters.AddWithValue("$now", now.ToString("O")); queue.ExecuteNonQuery(); }
        tx.Commit(); return new ZatcaChainRecord(id, draft.CompanyId, draft.BranchId, draft.EgsUnitId, draft.Uuid, draft.InvoiceNumber, built.InvoiceHashBase64, built.PreviousInvoiceHashBase64, draft.Kind.ToString(), "Pending", now);
    }

    public IReadOnlyList<ZatcaChainRecord> ClaimPending(Guid userId, Guid companyId, Guid branchId, int maxItems = 10, int maxAttempts = 8)
    {
        if (maxItems is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(maxItems));
        if (maxAttempts is < 1 or > 50) throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        using var c = Open(); using var tx = c.BeginTransaction(); Require(c, tx, userId, companyId, branchId, "zatca.integration.manage");
        var now = DateTimeOffset.UtcNow;
        var rows = new List<ZatcaChainRecord>();
        using (var q = c.CreateCommand())
        {
            q.Transaction = tx; q.CommandText = @"SELECT c.id,c.company_id,c.branch_id,c.egs_unit_id,c.uuid,c.invoice_number,c.invoice_hash_base64,c.previous_invoice_hash_base64,c.invoice_kind,o.submission_status,c.created_utc
FROM monarch_zatca_invoice_chain c JOIN monarch_zatca_submission_outbox o ON o.chain_record_id=c.id AND o.company_id=c.company_id
JOIN monarch_zatca_egs_units e ON e.id=c.egs_unit_id AND e.company_id=c.company_id AND e.branch_id=c.branch_id
WHERE c.company_id=$co AND c.branch_id=$br AND o.processed_utc IS NULL
AND e.lifecycle_state='Active' AND NULLIF(trim(e.csr_fingerprint),'') IS NOT NULL
AND NULLIF(trim(e.otp_transaction_reference),'') IS NOT NULL AND NULLIF(trim(e.csid_serial_number),'') IS NOT NULL
AND NULLIF(trim(e.csid_certificate_thumbprint),'') IS NOT NULL AND NULLIF(trim(e.secret_reference),'') IS NOT NULL
AND o.attempts<$attempts AND o.submission_status IN('Pending','Failed')
ORDER BY c.chain_sequence LIMIT $max;";
            q.Parameters.AddWithValue("$co", companyId.ToString()); q.Parameters.AddWithValue("$br", branchId.ToString()); q.Parameters.AddWithValue("$max", maxItems); q.Parameters.AddWithValue("$attempts", maxAttempts);
            using var r = q.ExecuteReader(); while (r.Read()) rows.Add(new ZatcaChainRecord(Guid.Parse(r.GetString(0)), Guid.Parse(r.GetString(1)), Guid.Parse(r.GetString(2)), Guid.Parse(r.GetString(3)), Guid.Parse(r.GetString(4)), r.GetString(5), r.GetString(6), r.GetString(7), r.GetString(8), r.GetString(9), DateTimeOffset.Parse(r.GetString(10))));
        }
        foreach (var row in rows)
        {
            using var claim = c.CreateCommand(); claim.Transaction = tx; claim.CommandText = @"UPDATE monarch_zatca_submission_outbox SET submission_status='Processing',processing_utc=$now,attempts=attempts+1
WHERE company_id=$co AND branch_id=$br AND chain_record_id=$id AND processed_utc IS NULL
AND submission_status IN('Pending','Failed');";
            claim.Parameters.AddWithValue("$now", now.ToString("O")); claim.Parameters.AddWithValue("$co", companyId.ToString()); claim.Parameters.AddWithValue("$br", branchId.ToString()); claim.Parameters.AddWithValue("$id", row.Id.ToString());
            if (claim.ExecuteNonQuery() != 1) throw new InvalidOperationException("ZATCA outbox claim changed concurrently; retry the batch.");
        }
        tx.Commit(); return rows;
    }

    public IReadOnlyList<ZatcaChainRecord> ListPending(Guid userId, Guid companyId, Guid branchId, int maxItems = 25)
    {
        if (maxItems is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(maxItems));
        using var c = Open(); using var tx = c.BeginTransaction(); Require(c, tx, userId, companyId, branchId, "reports.read");
        using var q = c.CreateCommand(); q.Transaction = tx; q.CommandText = @"SELECT c.id,c.company_id,c.branch_id,c.egs_unit_id,c.uuid,c.invoice_number,c.invoice_hash_base64,c.previous_invoice_hash_base64,c.invoice_kind,o.submission_status,c.created_utc
FROM monarch_zatca_invoice_chain c JOIN monarch_zatca_submission_outbox o ON o.chain_record_id=c.id AND o.company_id=c.company_id
WHERE c.company_id=$co AND c.branch_id=$br AND o.processed_utc IS NULL ORDER BY c.chain_sequence LIMIT $max;";
        q.Parameters.AddWithValue("$co", companyId.ToString()); q.Parameters.AddWithValue("$br", branchId.ToString()); q.Parameters.AddWithValue("$max", maxItems);
        var rows = new List<ZatcaChainRecord>();
        using (var r = q.ExecuteReader()) while (r.Read()) rows.Add(new ZatcaChainRecord(Guid.Parse(r.GetString(0)), Guid.Parse(r.GetString(1)), Guid.Parse(r.GetString(2)), Guid.Parse(r.GetString(3)), Guid.Parse(r.GetString(4)), r.GetString(5), r.GetString(6), r.GetString(7), r.GetString(8), r.GetString(9), DateTimeOffset.Parse(r.GetString(10))));
        tx.Commit(); return rows;
    }

    public (string Xml, string Hash, string Kind, Guid EgsUnitId) GetSubmissionPayload(Guid userId, Guid companyId, Guid branchId, Guid recordId)
    {
        using var c = Open(); using var tx = c.BeginTransaction(); Require(c, tx, userId, companyId, branchId, "reports.read");
        using var q = c.CreateCommand(); q.Transaction = tx; q.CommandText = @"SELECT c.invoice_xml,c.invoice_hash_base64,c.invoice_kind,c.egs_unit_id
FROM monarch_zatca_invoice_chain c JOIN monarch_zatca_egs_units e ON e.id=c.egs_unit_id AND e.company_id=c.company_id AND e.branch_id=c.branch_id
WHERE c.company_id=$co AND c.branch_id=$br AND c.id=$id AND e.lifecycle_state='Active'
AND NULLIF(trim(e.csr_fingerprint),'') IS NOT NULL AND NULLIF(trim(e.otp_transaction_reference),'') IS NOT NULL
AND NULLIF(trim(e.csid_serial_number),'') IS NOT NULL AND NULLIF(trim(e.csid_certificate_thumbprint),'') IS NOT NULL
AND NULLIF(trim(e.secret_reference),'') IS NOT NULL;";
        q.Parameters.AddWithValue("$co", companyId.ToString()); q.Parameters.AddWithValue("$br", branchId.ToString()); q.Parameters.AddWithValue("$id", recordId.ToString());
        (string, string, string, Guid) result;
        using (var r = q.ExecuteReader()) { if (!r.Read()) throw new InvalidOperationException("ZATCA invoice record not found or its EGS unit is not ready for submission."); result = (r.GetString(0), r.GetString(1), r.GetString(2), Guid.Parse(r.GetString(3))); }
        tx.Commit(); return result;
    }

    public (string Xml, string QrPayloadBase64) GetClearedDocument(Guid userId, Guid companyId, Guid branchId, Guid recordId)
    {
        using var c = Open(); using var tx = c.BeginTransaction(); Require(c, tx, userId, companyId, branchId, "reports.read");
        using var q = c.CreateCommand(); q.Transaction = tx; q.CommandText = "SELECT cleared_invoice_xml,cleared_qr_payload_base64,submission_status FROM monarch_zatca_invoice_chain WHERE company_id=$co AND branch_id=$br AND id=$id;";
        q.Parameters.AddWithValue("$co", companyId.ToString()); q.Parameters.AddWithValue("$br", branchId.ToString()); q.Parameters.AddWithValue("$id", recordId.ToString());
        (string, string) result;
        using (var r = q.ExecuteReader()) { if (!r.Read() || r.IsDBNull(0) || r.GetString(2) != "Cleared") throw new InvalidOperationException("A cleared ZATCA XML document is not available for this invoice."); result = (r.GetString(0), r.IsDBNull(1) ? string.Empty : r.GetString(1)); }
        tx.Commit(); return result;
    }

    public void CompleteSubmission(Guid userId, Guid companyId, Guid branchId, Guid recordId, string status, string? response,
        string? clearedInvoiceXml = null, string? clearedQrPayloadBase64 = null)
    {
        if (status is not ("Cleared" or "Reported" or "Rejected" or "Failed")) throw new ArgumentException("Invalid ZATCA submission state.");
        if (status == "Cleared" && string.IsNullOrWhiteSpace(clearedInvoiceXml)) throw new ArgumentException("A cleared standard invoice must retain the XML returned by FATOORA.");
        if (response != null && response.Length > 16000) response = response.Substring(0, 16000);
        using var c = Open(); using var tx = c.BeginTransaction(); Require(c, tx, userId, companyId, branchId, "zatca.integration.manage");
        using (var update = c.CreateCommand()) { update.Transaction = tx; update.CommandText = "UPDATE monarch_zatca_invoice_chain SET submission_status=$status,last_response=$response,cleared_invoice_xml=COALESCE($cleared,cleared_invoice_xml),cleared_qr_payload_base64=COALESCE($qr,cleared_qr_payload_base64),updated_utc=$now WHERE company_id=$co AND branch_id=$br AND id=$id AND submission_status IN('Pending','Failed');"; update.Parameters.AddWithValue("$status", status); update.Parameters.AddWithValue("$response", (object?)response ?? DBNull.Value); update.Parameters.AddWithValue("$cleared", (object?)clearedInvoiceXml ?? DBNull.Value); update.Parameters.AddWithValue("$qr", (object?)clearedQrPayloadBase64 ?? DBNull.Value); update.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O")); update.Parameters.AddWithValue("$co", companyId.ToString()); update.Parameters.AddWithValue("$br", branchId.ToString()); update.Parameters.AddWithValue("$id", recordId.ToString()); if (update.ExecuteNonQuery() != 1) throw new InvalidOperationException("Invoice is not in a submittable state."); }
        using (var queue = c.CreateCommand()) { queue.Transaction = tx; queue.CommandText = "UPDATE monarch_zatca_submission_outbox SET submission_status=$status,processed_utc=CASE WHEN $terminal=1 THEN $now ELSE NULL END,processing_utc=NULL,last_error=$error WHERE company_id=$co AND branch_id=$br AND chain_record_id=$id AND processed_utc IS NULL;"; queue.Parameters.AddWithValue("$status", status); queue.Parameters.AddWithValue("$terminal", status is "Cleared" or "Reported" or "Rejected" ? 1 : 0); queue.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O")); queue.Parameters.AddWithValue("$error", status is "Rejected" or "Failed" ? response ?? status : DBNull.Value); queue.Parameters.AddWithValue("$co", companyId.ToString()); queue.Parameters.AddWithValue("$br", branchId.ToString()); queue.Parameters.AddWithValue("$id", recordId.ToString()); queue.ExecuteNonQuery(); }
        using (var audit = c.CreateCommand()) { audit.Transaction = tx; audit.CommandText = "INSERT INTO monarch_company_audit_log(company_id,branch_id,actor_user_id,action,entity_type,entity_id,occurred_utc) VALUES($co,$br,$user,$action,'ZatcaInvoice',$id,$now);"; audit.Parameters.AddWithValue("$co", companyId.ToString()); audit.Parameters.AddWithValue("$br", branchId.ToString()); audit.Parameters.AddWithValue("$user", userId.ToString()); audit.Parameters.AddWithValue("$action", "ZatcaSubmission" + status); audit.Parameters.AddWithValue("$id", recordId.ToString()); audit.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O")); audit.ExecuteNonQuery(); }
        tx.Commit();
    }

    /// <summary>Audits a blocked EGS readiness evaluation; no invoice state or attempt counter is changed.</summary>
    public void AuditEgsReadinessBlocked(Guid userId, Guid companyId, Guid branchId, Guid egsUnitId, Guid recordId, string reasons)
    {
        if (string.IsNullOrWhiteSpace(reasons)) throw new ArgumentException("A blocking reason is required.", nameof(reasons));
        if (reasons.Length > 3000) reasons = reasons.Substring(0, 3000);
        using var c = Open(); using var tx = c.BeginTransaction(); Require(c, tx, userId, companyId, branchId, "zatca.integration.manage");
        using var q = c.CreateCommand(); q.Transaction = tx;
        q.CommandText = @"INSERT INTO monarch_company_audit_log(company_id,branch_id,actor_user_id,action,entity_type,entity_id,details,occurred_utc)
VALUES($co,$br,$user,'ZatcaEgsSubmissionBlocked','ZatcaInvoice',$record,$details,$now);";
        q.Parameters.AddWithValue("$co", companyId.ToString()); q.Parameters.AddWithValue("$br", branchId.ToString()); q.Parameters.AddWithValue("$user", userId.ToString());
        q.Parameters.AddWithValue("$record", recordId.ToString()); q.Parameters.AddWithValue("$details", "EGS=" + egsUnitId + "; " + reasons); q.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O")); q.ExecuteNonQuery(); tx.Commit();
    }

    /// <summary>Records an indeterminate transport outcome. Processing is deliberately retained to prevent blind resend.</summary>
    public void MarkSubmissionOutcomeUnknown(Guid userId, Guid companyId, Guid branchId, Guid recordId, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) reason = "Transport outcome is indeterminate; manual reconciliation required.";
        if (reason.Length > 3000) reason = reason.Substring(0, 3000);
        using var c = Open(); using var tx = c.BeginTransaction(); Require(c, tx, userId, companyId, branchId, "zatca.integration.manage");
        using var q = c.CreateCommand(); q.Transaction = tx;
        q.CommandText = @"INSERT INTO monarch_company_audit_log(company_id,branch_id,actor_user_id,action,entity_type,entity_id,details,occurred_utc)
VALUES($co,$br,$user,'ZatcaSubmissionOutcomeUnknown','ZatcaInvoice',$id,$details,$now);";
        q.Parameters.AddWithValue("$co", companyId.ToString()); q.Parameters.AddWithValue("$br", branchId.ToString()); q.Parameters.AddWithValue("$user", userId.ToString()); q.Parameters.AddWithValue("$id", recordId.ToString());
        q.Parameters.AddWithValue("$details", reason); q.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O")); q.ExecuteNonQuery(); tx.Commit();
    }

    /// <summary>Returns tenant-scoped submission counts for an operational dashboard.</summary>
    public ZatcaSubmissionDashboard GetDashboard(Guid userId, Guid companyId, Guid branchId)
    {
        using var c = Open(); using var tx = c.BeginTransaction(); Require(c, tx, userId, companyId, branchId, "reports.read");
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        using (var q = c.CreateCommand())
        {
            q.Transaction = tx; q.CommandText = "SELECT submission_status,COUNT(*) FROM monarch_zatca_submission_outbox WHERE company_id=$co AND branch_id=$br GROUP BY submission_status;";
            q.Parameters.AddWithValue("$co", companyId.ToString()); q.Parameters.AddWithValue("$br", branchId.ToString());
            using var r = q.ExecuteReader(); while (r.Read()) counts[r.GetString(0)] = r.GetInt32(1);
        }
        tx.Commit();
        int Count(string status) => counts.TryGetValue(status, out var value) ? value : 0;
        return new ZatcaSubmissionDashboard(Count("Pending"), Count("Processing"), Count("Failed"), Count("Rejected"), Count("Cleared"), Count("Reported"));
    }

    /// <summary>Lists recent submission outcomes without exposing another tenant or branch.</summary>
    public IReadOnlyList<ZatcaSubmissionHistoryItem> ListSubmissionHistory(Guid userId, Guid companyId, Guid branchId, int limit = 100)
    {
        if (limit is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(limit));
        using var c = Open(); using var tx = c.BeginTransaction(); Require(c, tx, userId, companyId, branchId, "reports.read");
        using var q = c.CreateCommand(); q.Transaction = tx;
        q.CommandText = @"SELECT c.id,c.egs_unit_id,c.uuid,c.invoice_number,c.invoice_kind,o.submission_status,o.attempts,c.last_response,c.created_utc,o.processed_utc
FROM monarch_zatca_invoice_chain c JOIN monarch_zatca_submission_outbox o ON o.company_id=c.company_id AND o.chain_record_id=c.id
WHERE c.company_id=$co AND c.branch_id=$br ORDER BY c.created_utc DESC LIMIT $limit;";
        q.Parameters.AddWithValue("$co", companyId.ToString()); q.Parameters.AddWithValue("$br", branchId.ToString()); q.Parameters.AddWithValue("$limit", limit);
        var rows = new List<ZatcaSubmissionHistoryItem>();
        using (var r = q.ExecuteReader()) while (r.Read()) rows.Add(new ZatcaSubmissionHistoryItem(Guid.Parse(r.GetString(0)), Guid.Parse(r.GetString(1)), Guid.Parse(r.GetString(2)), r.GetString(3), r.GetString(4), r.GetString(5), r.GetInt32(6), r.IsDBNull(7) ? null : r.GetString(7), DateTimeOffset.Parse(r.GetString(8)), r.IsDBNull(9) ? null : DateTimeOffset.Parse(r.GetString(9))));
        tx.Commit(); return rows;
    }

    /// <summary>Explicitly requeues a failed item. Rejected invoices are never blindly retried.</summary>
    public void RetryFailedSubmission(Guid userId, Guid companyId, Guid branchId, Guid recordId, int maxAttempts = 8)
    {
        if (maxAttempts is < 1 or > 50) throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        using var c = Open(); using var tx = c.BeginTransaction(); Require(c, tx, userId, companyId, branchId, "zatca.integration.manage");
        using (var q = c.CreateCommand())
        {
            q.Transaction = tx; q.CommandText = @"UPDATE monarch_zatca_invoice_chain SET submission_status='Pending',last_response=NULL,updated_utc=$now
WHERE company_id=$co AND branch_id=$br AND id=$id AND submission_status='Failed'
AND EXISTS(SELECT 1 FROM monarch_zatca_submission_outbox o WHERE o.company_id=$co AND o.branch_id=$br AND o.chain_record_id=$id AND o.submission_status='Failed' AND o.attempts<$attempts AND o.processed_utc IS NULL);";
            q.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O")); q.Parameters.AddWithValue("$co", companyId.ToString()); q.Parameters.AddWithValue("$br", branchId.ToString()); q.Parameters.AddWithValue("$id", recordId.ToString()); q.Parameters.AddWithValue("$attempts", maxAttempts);
            if (q.ExecuteNonQuery() != 1) throw new InvalidOperationException("Only a retryable Failed invoice below the attempt limit can be requeued. Rejected invoices require correction and a new compliant document.");
        }
        using (var q = c.CreateCommand())
        {
            q.Transaction = tx; q.CommandText = "UPDATE monarch_zatca_submission_outbox SET submission_status='Pending',processing_utc=NULL,last_error=NULL WHERE company_id=$co AND branch_id=$br AND chain_record_id=$id AND submission_status='Failed' AND processed_utc IS NULL;";
            q.Parameters.AddWithValue("$co", companyId.ToString()); q.Parameters.AddWithValue("$br", branchId.ToString()); q.Parameters.AddWithValue("$id", recordId.ToString());
            if (q.ExecuteNonQuery() != 1) throw new InvalidOperationException("ZATCA outbox item is not retryable.");
        }
        using (var q = c.CreateCommand())
        {
            q.Transaction = tx; q.CommandText = "INSERT INTO monarch_company_audit_log(company_id,branch_id,actor_user_id,action,entity_type,entity_id,occurred_utc) VALUES($co,$br,$user,'ZatcaSubmissionRetry','ZatcaInvoice',$id,$now);";
            q.Parameters.AddWithValue("$co", companyId.ToString()); q.Parameters.AddWithValue("$br", branchId.ToString()); q.Parameters.AddWithValue("$user", userId.ToString()); q.Parameters.AddWithValue("$id", recordId.ToString()); q.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O")); q.ExecuteNonQuery();
        }
        tx.Commit();
    }

    private static void Require(SqliteConnection c, SqliteTransaction tx, Guid user, Guid co, Guid br, string permission)
    {
        using var q = c.CreateCommand(); q.Transaction = tx; q.CommandText = @"SELECT COUNT(*) FROM monarch_users u JOIN monarch_user_company_access ca ON ca.user_id=u.id AND ca.company_id=$co AND ca.is_active=1 JOIN monarch_user_branch_access ba ON ba.user_id=u.id AND ba.company_id=$co AND ba.branch_id=$br AND ba.is_active=1 JOIN monarch_branches b ON b.company_id=ba.company_id AND b.id=ba.branch_id AND b.is_active=1 JOIN monarch_user_roles ur ON ur.user_id=u.id JOIN monarch_role_permissions rp ON rp.role_id=ur.role_id AND rp.permission_code=$permission WHERE u.id=$user AND u.is_active=1 AND EXISTS(SELECT 1 FROM monarch_companies co WHERE co.id=$co AND co.is_active=1);";
        q.Parameters.AddWithValue("$co", co.ToString()); q.Parameters.AddWithValue("$br", br.ToString()); q.Parameters.AddWithValue("$user", user.ToString()); q.Parameters.AddWithValue("$permission", permission); if (Convert.ToInt64(q.ExecuteScalar()) < 1) throw new UnauthorizedAccessException("User is not authorized for this ZATCA operation in the selected tenant/branch.");
    }
    private SqliteConnection Open() { var c = new SqliteConnection(_connectionString); c.Open(); using var p = c.CreateCommand(); p.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;"; p.ExecuteNonQuery(); return c; }
}
