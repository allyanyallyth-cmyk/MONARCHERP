using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;

namespace Monarch.Core.Zatca;

public enum ZatcaEgsLifecycleState { Draft, CsrGenerated, CompliancePending, CompliancePassed, ProductionPending, Active, Suspended, Revoked }
public enum ZatcaEnvironment { Simulation, Production }

/// <summary>Non-secret EGS registration metadata. Never place OTPs, private keys, or client secrets here.</summary>
public sealed record ZatcaEgsRegistration(Guid Id, Guid CompanyId, Guid BranchId, string UnitName,
    string VatNumber, ZatcaEnvironment Environment, ZatcaEgsLifecycleState State,
    string? OtpTransactionReference, string? CsrFingerprint, string? CsidSerialNumber,
    string? CsidCertificateThumbprint, string? SecretReference, DateTimeOffset CreatedUtc, DateTimeOffset UpdatedUtc);
public sealed record ZatcaEgsLifecycleEvent(Guid Id, Guid EgsUnitId, string FromState, string ToState,
    string? Note, DateTimeOffset OccurredUtc, Guid ActorUserId);
public sealed record ZatcaEgsReadiness(Guid EgsUnitId, ZatcaEgsLifecycleState State,
    bool HasCsrFingerprint, bool HasOtpTransactionReference, bool HasCsidSerial,
    bool HasCertificateThumbprint, bool HasExternalSecretReference, bool CanSubmitInvoices,
    IReadOnlyList<string> BlockingReasons);

/// <summary>Secret vault abstraction. Implementations must protect secrets using OS-backed storage.</summary>
public interface IZatcaSecretStore
{
    string Store(string purpose, ReadOnlyMemory<byte> secret);
    ReadOnlyMemory<byte> Retrieve(string reference, string purpose);
    void Delete(string reference, string purpose);
}

/// <summary>Tenant-scoped EGS registry and audited onboarding lifecycle. Schema is provisioned separately.</summary>
public sealed class ZatcaEgsRegistry
{
    private readonly string _cs;
    public ZatcaEgsRegistry(string databasePath)
    {
        var full = Path.GetFullPath(databasePath); Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        _cs = new SqliteConnectionStringBuilder { DataSource = full, ForeignKeys = true }.ToString();
    }

    public ZatcaEgsRegistration Create(Guid userId, Guid companyId, Guid branchId, string unitName,
        string vatNumber, ZatcaEnvironment environment)
    {
        if (!Enum.IsDefined(typeof(ZatcaEnvironment), environment)) throw new ArgumentOutOfRangeException(nameof(environment));
        if (string.IsNullOrWhiteSpace(unitName) || unitName.Length > 120) throw new ArgumentException("EGS unit name is required and must not exceed 120 characters.", nameof(unitName));
        if (string.IsNullOrWhiteSpace(vatNumber) || vatNumber.Length != 15 || !vatNumber.StartsWith("3", StringComparison.Ordinal)) throw new ArgumentException("A 15-character Saudi VAT number beginning with 3 is required.", nameof(vatNumber));
        var id = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        using var c = OpenConnection(); using var tx = c.BeginTransaction(); Require(c, tx, userId, companyId, branchId);
        using (var q = c.CreateCommand())
        {
            q.Transaction = tx; q.CommandText = @"INSERT INTO monarch_zatca_egs_units(id,company_id,branch_id,unit_name,vat_number,environment,lifecycle_state,created_utc,updated_utc,created_by)
VALUES($id,$co,$br,$name,$vat,$env,'Draft',$now,$now,$user);";
            q.Parameters.AddWithValue("$id", id.ToString()); q.Parameters.AddWithValue("$co", companyId.ToString()); q.Parameters.AddWithValue("$br", branchId.ToString()); q.Parameters.AddWithValue("$name", unitName.Trim()); q.Parameters.AddWithValue("$vat", vatNumber); q.Parameters.AddWithValue("$env", environment.ToString()); q.Parameters.AddWithValue("$now", now.ToString("O")); q.Parameters.AddWithValue("$user", userId.ToString()); q.ExecuteNonQuery();
        }
        Audit(c, tx, userId, companyId, branchId, id, "", "Draft", "EGS unit created", now); tx.Commit();
        return new ZatcaEgsRegistration(id, companyId, branchId, unitName.Trim(), vatNumber, environment, ZatcaEgsLifecycleState.Draft, null, null, null, null, null, now, now);
    }

    public void RecordCsrMetadata(Guid userId, Guid companyId, Guid branchId, Guid egsUnitId,
        string csrFingerprint, string? otpTransactionReference)
    {
        if (string.IsNullOrWhiteSpace(csrFingerprint) || csrFingerprint.Length > 256) throw new ArgumentException("CSR fingerprint is required.", nameof(csrFingerprint));
        if (otpTransactionReference?.Length > 200) throw new ArgumentException("OTP transaction reference is too long.", nameof(otpTransactionReference));
        Transition(userId, companyId, branchId, egsUnitId, ZatcaEgsLifecycleState.CsrGenerated,
            "CSR metadata recorded; no CSR/private key/OTP is stored", csrFingerprint, otpTransactionReference, null, null, null);
    }

    public void Transition(Guid userId, Guid companyId, Guid branchId, Guid egsUnitId,
        ZatcaEgsLifecycleState target, string? note = null, string? csrFingerprint = null,
        string? otpTransactionReference = null, string? csidSerialNumber = null,
        string? csidCertificateThumbprint = null, string? secretReference = null)
    {
        if (note?.Length > 1000 || csidSerialNumber?.Length > 200 || csidCertificateThumbprint?.Length > 256 || secretReference?.Length > 500)
            throw new ArgumentException("EGS metadata exceeds the allowed length.");
        using var c = OpenConnection(); using var tx = c.BeginTransaction(); Require(c, tx, userId, companyId, branchId);
        string current;
        using (var read = c.CreateCommand())
        {
            read.Transaction = tx; read.CommandText = "SELECT lifecycle_state FROM monarch_zatca_egs_units WHERE id=$id AND company_id=$co AND branch_id=$br;";
            read.Parameters.AddWithValue("$id", egsUnitId.ToString()); read.Parameters.AddWithValue("$co", companyId.ToString()); read.Parameters.AddWithValue("$br", branchId.ToString());
            current = read.ExecuteScalar() as string ?? throw new KeyNotFoundException("EGS unit was not found in the selected company and branch.");
        }
        if (!Allowed(current, target)) throw new InvalidOperationException($"Invalid EGS lifecycle transition: {current} -> {target}.");
        if (target == ZatcaEgsLifecycleState.Active && (string.IsNullOrWhiteSpace(csidSerialNumber) || string.IsNullOrWhiteSpace(csidCertificateThumbprint) || string.IsNullOrWhiteSpace(secretReference)))
            throw new InvalidOperationException("Activating an EGS requires CSID serial, certificate thumbprint, and an external secret reference.");
        var now = DateTimeOffset.UtcNow;
        using (var update = c.CreateCommand())
        {
            update.Transaction = tx; update.CommandText = @"UPDATE monarch_zatca_egs_units SET lifecycle_state=$state,csr_fingerprint=COALESCE($fp,csr_fingerprint),otp_transaction_reference=COALESCE($otp,otp_transaction_reference),csid_serial_number=COALESCE($serial,csid_serial_number),csid_certificate_thumbprint=COALESCE($thumb,csid_certificate_thumbprint),secret_reference=COALESCE($secret,secret_reference),updated_utc=$now WHERE id=$id AND company_id=$co AND branch_id=$br;";
            update.Parameters.AddWithValue("$state", target.ToString()); update.Parameters.AddWithValue("$fp", (object?)csrFingerprint ?? DBNull.Value); update.Parameters.AddWithValue("$otp", (object?)otpTransactionReference ?? DBNull.Value); update.Parameters.AddWithValue("$serial", (object?)csidSerialNumber ?? DBNull.Value); update.Parameters.AddWithValue("$thumb", (object?)csidCertificateThumbprint ?? DBNull.Value); update.Parameters.AddWithValue("$secret", (object?)secretReference ?? DBNull.Value); update.Parameters.AddWithValue("$now", now.ToString("O")); update.Parameters.AddWithValue("$id", egsUnitId.ToString()); update.Parameters.AddWithValue("$co", companyId.ToString()); update.Parameters.AddWithValue("$br", branchId.ToString()); if (update.ExecuteNonQuery() != 1) throw new InvalidOperationException("EGS lifecycle update did not affect exactly one row.");
        }
        Audit(c, tx, userId, companyId, branchId, egsUnitId, current, target.ToString(), note, now); tx.Commit();
    }

    public IReadOnlyList<ZatcaEgsRegistration> List(Guid userId, Guid companyId, Guid branchId)
    {
        using var c = OpenConnection(); using var tx = c.BeginTransaction(); Require(c, tx, userId, companyId, branchId);
        using var q = c.CreateCommand(); q.Transaction = tx; q.CommandText = @"SELECT id,unit_name,vat_number,environment,lifecycle_state,otp_transaction_reference,csr_fingerprint,csid_serial_number,csid_certificate_thumbprint,secret_reference,created_utc,updated_utc FROM monarch_zatca_egs_units WHERE company_id=$co AND branch_id=$br ORDER BY created_utc;";
        q.Parameters.AddWithValue("$co", companyId.ToString()); q.Parameters.AddWithValue("$br", branchId.ToString()); var rows = new List<ZatcaEgsRegistration>();
        using (var r = q.ExecuteReader()) while (r.Read()) rows.Add(new ZatcaEgsRegistration(Guid.Parse(r.GetString(0)), companyId, branchId, r.GetString(1), r.GetString(2), Enum.Parse<ZatcaEnvironment>(r.GetString(3)), Enum.Parse<ZatcaEgsLifecycleState>(r.GetString(4)), Null(r,5), Null(r,6), Null(r,7), Null(r,8), Null(r,9), DateTimeOffset.Parse(r.GetString(10)), DateTimeOffset.Parse(r.GetString(11))));
        tx.Commit(); return rows;
    }

    /// <summary>Returns a non-secret readiness assessment. This method never retrieves or exposes vault material.</summary>
    public ZatcaEgsReadiness EvaluateReadiness(Guid userId, Guid companyId, Guid branchId, Guid egsUnitId)
    {
        using var c = OpenConnection(); using var tx = c.BeginTransaction(); Require(c, tx, userId, companyId, branchId);
        string state; string? csr; string? otp; string? serial; string? thumb; string? secret;
        using (var q = c.CreateCommand())
        {
            q.Transaction = tx;
            q.CommandText = @"SELECT lifecycle_state,csr_fingerprint,otp_transaction_reference,csid_serial_number,csid_certificate_thumbprint,secret_reference
FROM monarch_zatca_egs_units WHERE id=$id AND company_id=$co AND branch_id=$br;";
            q.Parameters.AddWithValue("$id", egsUnitId.ToString()); q.Parameters.AddWithValue("$co", companyId.ToString()); q.Parameters.AddWithValue("$br", branchId.ToString());
            using var r = q.ExecuteReader();
            if (!r.Read()) throw new KeyNotFoundException("EGS unit was not found in the selected company and branch.");
            state = r.GetString(0); csr = Null(r,1); otp = Null(r,2); serial = Null(r,3); thumb = Null(r,4); secret = Null(r,5);
        }
        var reasons = new List<string>();
        if (string.IsNullOrWhiteSpace(csr)) reasons.Add("CSR fingerprint is missing.");
        if (string.IsNullOrWhiteSpace(otp)) reasons.Add("OTP transaction reference is missing.");
        if (string.IsNullOrWhiteSpace(serial)) reasons.Add("CSID serial number is missing.");
        if (string.IsNullOrWhiteSpace(thumb)) reasons.Add("CSID certificate thumbprint is missing.");
        if (string.IsNullOrWhiteSpace(secret)) reasons.Add("External secret-store reference is missing.");
        if (!string.Equals(state, ZatcaEgsLifecycleState.Active.ToString(), StringComparison.Ordinal)) reasons.Add("EGS lifecycle state is not Active.");
        var canSubmit = reasons.Count == 0;
        tx.Commit();
        return new ZatcaEgsReadiness(egsUnitId, Enum.Parse<ZatcaEgsLifecycleState>(state), !string.IsNullOrWhiteSpace(csr),
            !string.IsNullOrWhiteSpace(otp), !string.IsNullOrWhiteSpace(serial), !string.IsNullOrWhiteSpace(thumb),
            !string.IsNullOrWhiteSpace(secret), canSubmit, reasons);
    }

    public IReadOnlyList<ZatcaEgsLifecycleEvent> History(Guid userId, Guid companyId, Guid branchId, Guid egsUnitId)
    {
        using var c = OpenConnection(); using var tx = c.BeginTransaction(); Require(c, tx, userId, companyId, branchId);
        using var q = c.CreateCommand(); q.Transaction = tx; q.CommandText = "SELECT id,from_state,to_state,note,occurred_utc,actor_user_id FROM monarch_zatca_egs_lifecycle WHERE company_id=$co AND branch_id=$br AND egs_unit_id=$id ORDER BY occurred_utc;";
        q.Parameters.AddWithValue("$co", companyId.ToString()); q.Parameters.AddWithValue("$br", branchId.ToString()); q.Parameters.AddWithValue("$id", egsUnitId.ToString()); var rows = new List<ZatcaEgsLifecycleEvent>();
        using (var r = q.ExecuteReader()) while (r.Read()) rows.Add(new ZatcaEgsLifecycleEvent(Guid.Parse(r.GetString(0)), egsUnitId, r.GetString(1), r.GetString(2), Null(r,3), DateTimeOffset.Parse(r.GetString(4)), Guid.Parse(r.GetString(5))));
        tx.Commit(); return rows;
    }

    private static bool Allowed(string from, ZatcaEgsLifecycleState to) => from switch
    {
        "Draft" => to == ZatcaEgsLifecycleState.CsrGenerated || to == ZatcaEgsLifecycleState.Revoked,
        "CsrGenerated" => to == ZatcaEgsLifecycleState.CompliancePending || to == ZatcaEgsLifecycleState.Draft || to == ZatcaEgsLifecycleState.Revoked,
        "CompliancePending" => to == ZatcaEgsLifecycleState.CompliancePassed || to == ZatcaEgsLifecycleState.CsrGenerated || to == ZatcaEgsLifecycleState.Revoked,
        "CompliancePassed" => to == ZatcaEgsLifecycleState.ProductionPending || to == ZatcaEgsLifecycleState.Revoked,
        "ProductionPending" => to == ZatcaEgsLifecycleState.Active || to == ZatcaEgsLifecycleState.CompliancePassed || to == ZatcaEgsLifecycleState.Revoked,
        "Active" => to == ZatcaEgsLifecycleState.Suspended || to == ZatcaEgsLifecycleState.Revoked,
        "Suspended" => to == ZatcaEgsLifecycleState.Active || to == ZatcaEgsLifecycleState.Revoked,
        _ => false
    };
    private static string? Null(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private SqliteConnection OpenConnection() { var c = new SqliteConnection(_cs); c.Open(); return c; }
    private void Require(SqliteConnection c, SqliteTransaction tx, Guid user, Guid co, Guid br)
    {
        using var q = c.CreateCommand(); q.Transaction = tx; q.CommandText = @"SELECT COUNT(*) FROM monarch_users u JOIN monarch_user_company_access ca ON ca.user_id=u.id AND ca.company_id=$co AND ca.is_active=1 JOIN monarch_user_branch_access ba ON ba.user_id=u.id AND ba.company_id=$co AND ba.branch_id=$br AND ba.is_active=1 JOIN monarch_branches b ON b.company_id=ba.company_id AND b.id=ba.branch_id AND b.is_active=1 JOIN monarch_user_roles ur ON ur.user_id=u.id JOIN monarch_role_permissions rp ON rp.role_id=ur.role_id AND rp.permission_code='zatca.integration.manage' WHERE u.id=$user AND u.is_active=1 AND EXISTS(SELECT 1 FROM monarch_companies co WHERE co.id=$co AND co.is_active=1);";
        q.Parameters.AddWithValue("$co", co.ToString()); q.Parameters.AddWithValue("$br", br.ToString()); q.Parameters.AddWithValue("$user", user.ToString()); if (Convert.ToInt64(q.ExecuteScalar()) < 1) throw new UnauthorizedAccessException("User lacks ZATCA integration management permission in this tenant/branch.");
    }
    private static void Audit(SqliteConnection c, SqliteTransaction tx, Guid user, Guid co, Guid br, Guid id, string from, string to, string? note, DateTimeOffset now)
    {
        using var q = c.CreateCommand(); q.Transaction = tx; q.CommandText = "INSERT INTO monarch_zatca_egs_lifecycle(id,company_id,branch_id,egs_unit_id,from_state,to_state,note,occurred_utc,actor_user_id) VALUES($event,$co,$br,$id,$from,$to,$note,$now,$user);";
        q.Parameters.AddWithValue("$event", Guid.NewGuid().ToString()); q.Parameters.AddWithValue("$co", co.ToString()); q.Parameters.AddWithValue("$br", br.ToString()); q.Parameters.AddWithValue("$id", id.ToString()); q.Parameters.AddWithValue("$from", from); q.Parameters.AddWithValue("$to", to); q.Parameters.AddWithValue("$note", (object?)note ?? DBNull.Value); q.Parameters.AddWithValue("$now", now.ToString("O")); q.Parameters.AddWithValue("$user", user.ToString()); q.ExecuteNonQuery();
    }
}
