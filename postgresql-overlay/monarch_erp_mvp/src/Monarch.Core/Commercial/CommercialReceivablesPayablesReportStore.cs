using Microsoft.Data.Sqlite;
using System.Globalization;

namespace Monarch.Core.Commercial;

public enum CommercialBalanceSide { CustomerReceivable, SupplierPayable }
public sealed record CommercialAgingRow(Guid PartyId, string PartyCode, string PartyName, Guid DocumentId,
    string DocumentNumber, DateOnly DocumentDate, DateOnly DueDate, string CurrencyCode, int CurrencyScale,
    long DocumentTotalMinor, long PaidAsOfMinor, long OutstandingMinor, int DaysOverdue,
    long CurrentMinor, long Days1To30Minor, long Days31To60Minor, long Days61To90Minor, long Over90Minor);
public sealed record CommercialStatementLine(DateOnly Date, string Reference, string Description,
    string CurrencyCode, int CurrencyScale, long DebitMinor, long CreditMinor, long RunningBalanceMinor);

/// <summary>Tenant- and branch-scoped read-only AR/AP reports based on posted documents and allocations.</summary>
public sealed class CommercialReceivablesPayablesReportStore
{
    private readonly string _connectionString;
    public CommercialReceivablesPayablesReportStore(string databasePath)
    {
        var full = Path.GetFullPath(databasePath); Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = full, ForeignKeys = true, Mode = SqliteOpenMode.ReadOnly }.ToString();
    }

    public IReadOnlyList<CommercialAgingRow> GetAging(Guid userId, Guid companyId, Guid branchId,
        CommercialBalanceSide side, DateOnly asOfDate, Guid? partyId = null)
    {
        ValidateSide(side);
        using var c = Open(); using var tx = c.BeginTransaction();
        RequireReportAccess(c, tx, userId, companyId, branchId);
        using var q = c.CreateCommand(); q.Transaction = tx;
        q.CommandText = @"WITH invoice_rows AS (
 SELECT p.id party_id,p.code party_code,p.display_name party_name,d.id document_id,d.document_number,
 date(d.created_utc) document_date,date(d.created_utc,'+' || p.payment_terms_days || ' days') due_date,
 d.currency_code,d.currency_scale,d.total_minor,
 COALESCE((SELECT SUM(a.amount_minor) FROM monarch_commercial_payment_allocations a
   JOIN monarch_commercial_payments pay ON pay.company_id=a.company_id AND pay.id=a.payment_id
   WHERE a.company_id=d.company_id AND a.document_id=d.id AND pay.company_id=d.company_id
     AND pay.branch_id=$br AND pay.status='Posted' AND date(pay.payment_date)<=date($asof)),0) paid_minor
 FROM monarch_commercial_documents d JOIN monarch_parties p ON p.company_id=d.company_id AND p.id=d.party_id
 WHERE d.company_id=$co AND d.branch_id=$br AND d.status='Posted' AND d.document_type=$doctype
 AND date(d.created_utc)<=date($asof) AND ($party IS NULL OR p.id=$party)
), balances AS (
 SELECT *,total_minor-paid_minor outstanding_minor,
 CAST(julianday(date($asof))-julianday(due_date) AS INTEGER) overdue_days FROM invoice_rows
)
SELECT party_id,party_code,party_name,document_id,document_number,document_date,due_date,currency_code,currency_scale,
 total_minor,paid_minor,outstanding_minor,overdue_days,
 CASE WHEN overdue_days<=0 THEN outstanding_minor ELSE 0 END,
 CASE WHEN overdue_days BETWEEN 1 AND 30 THEN outstanding_minor ELSE 0 END,
 CASE WHEN overdue_days BETWEEN 31 AND 60 THEN outstanding_minor ELSE 0 END,
 CASE WHEN overdue_days BETWEEN 61 AND 90 THEN outstanding_minor ELSE 0 END,
 CASE WHEN overdue_days>90 THEN outstanding_minor ELSE 0 END
FROM balances WHERE outstanding_minor>0 ORDER BY party_code,document_date,document_number;";
        q.Parameters.AddWithValue("$co", companyId.ToString()); q.Parameters.AddWithValue("$br", branchId.ToString());
        q.Parameters.AddWithValue("$doctype", side == CommercialBalanceSide.CustomerReceivable ? "Sale" : "Purchase");
        q.Parameters.AddWithValue("$asof", asOfDate.ToString("yyyy-MM-dd"));
        q.Parameters.AddWithValue("$party", partyId.HasValue ? partyId.Value.ToString() : DBNull.Value);
        var rows = new List<CommercialAgingRow>(); using var r = q.ExecuteReader();
        while (r.Read()) rows.Add(new CommercialAgingRow(Guid.Parse(r.GetString(0)),r.GetString(1),r.GetString(2),Guid.Parse(r.GetString(3)),r.GetString(4),
            DateOnly.ParseExact(r.GetString(5),"yyyy-MM-dd",CultureInfo.InvariantCulture),DateOnly.ParseExact(r.GetString(6),"yyyy-MM-dd",CultureInfo.InvariantCulture),r.GetString(7),r.GetInt32(8),r.GetInt64(9),r.GetInt64(10),r.GetInt64(11),r.GetInt32(12),
            r.GetInt64(13),r.GetInt64(14),r.GetInt64(15),r.GetInt64(16),r.GetInt64(17)));
        tx.Commit();
        return rows;
    }

    public IReadOnlyList<CommercialStatementLine> GetStatement(Guid userId, Guid companyId, Guid branchId,
        CommercialBalanceSide side, Guid partyId, DateOnly fromDate, DateOnly toDate)
    {
        ValidateSide(side);
        if (partyId == Guid.Empty || toDate < fromDate) throw new ArgumentException("A party and a valid date range are required.");
        using var c = Open(); using var tx = c.BeginTransaction();
        RequireReportAccess(c, tx, userId, companyId, branchId);
        using var q = c.CreateCommand(); q.Transaction = tx;
        q.CommandText = @"WITH entries AS (
 SELECT date(d.created_utc) entry_date,d.document_number reference,
 CASE WHEN d.document_type='Sale' THEN 'Sales invoice' ELSE 'Purchase invoice' END description,
 d.currency_code,d.currency_scale,d.total_minor amount_minor,0 is_payment,d.id source_id
 FROM monarch_commercial_documents d WHERE d.company_id=$co AND d.branch_id=$br AND d.party_id=$party
 AND d.status='Posted' AND d.document_type=$doctype AND date(d.created_utc)<=date($to)
 UNION ALL
 SELECT date(pay.payment_date),pay.payment_number,
 CASE WHEN pay.payment_type='CustomerReceipt' THEN 'Customer receipt' ELSE 'Supplier payment' END,
 pay.currency_code,pay.currency_scale,a.amount_minor,1,pay.id
 FROM monarch_commercial_payment_allocations a
 JOIN monarch_commercial_payments pay ON pay.company_id=a.company_id AND pay.id=a.payment_id
 JOIN monarch_commercial_documents d ON d.company_id=a.company_id AND d.id=a.document_id
 WHERE a.company_id=$co AND pay.company_id=$co AND pay.branch_id=$br AND pay.party_id=$party AND pay.status='Posted'
 AND d.company_id=$co AND d.branch_id=$br AND d.party_id=$party AND d.status='Posted' AND d.document_type=$doctype AND date(pay.payment_date)<=date($to)
), ordered AS (
 SELECT *,CASE WHEN is_payment=0 THEN amount_minor ELSE -amount_minor END signed_amount,
 ROW_NUMBER() OVER(PARTITION BY currency_code,currency_scale ORDER BY entry_date, is_payment, reference, source_id) rn
 FROM entries
), balanced AS (
 SELECT *,SUM(signed_amount) OVER(PARTITION BY currency_code,currency_scale ORDER BY entry_date,is_payment,reference,source_id ROWS UNBOUNDED PRECEDING) running_balance
 FROM ordered
)
SELECT entry_date,reference,description,currency_code,currency_scale,
 CASE WHEN signed_amount>0 THEN signed_amount ELSE 0 END debit_minor,
 CASE WHEN signed_amount<0 THEN -signed_amount ELSE 0 END credit_minor,running_balance
FROM balanced WHERE date(entry_date)>=date($from) ORDER BY entry_date,is_payment,reference,source_id;";
        q.Parameters.AddWithValue("$co", companyId.ToString()); q.Parameters.AddWithValue("$br", branchId.ToString()); q.Parameters.AddWithValue("$party", partyId.ToString());
        q.Parameters.AddWithValue("$doctype", side == CommercialBalanceSide.CustomerReceivable ? "Sale" : "Purchase");
        q.Parameters.AddWithValue("$from", fromDate.ToString("yyyy-MM-dd")); q.Parameters.AddWithValue("$to", toDate.ToString("yyyy-MM-dd"));
        var rows = new List<CommercialStatementLine>(); using var r = q.ExecuteReader();
        while (r.Read()) rows.Add(new CommercialStatementLine(DateOnly.ParseExact(r.GetString(0),"yyyy-MM-dd",CultureInfo.InvariantCulture),r.GetString(1),r.GetString(2),r.GetString(3),r.GetInt32(4),r.GetInt64(5),r.GetInt64(6),r.GetInt64(7)));
        tx.Commit();
        return rows;
    }

    private static void ValidateSide(CommercialBalanceSide side)
    {
        if (side != CommercialBalanceSide.CustomerReceivable && side != CommercialBalanceSide.SupplierPayable)
            throw new ArgumentOutOfRangeException(nameof(side), "نوع تقرير الذمم غير صالح.");
    }

    private static void RequireReportAccess(SqliteConnection c, SqliteTransaction tx, Guid user, Guid company, Guid branch)
    {
        using var q = c.CreateCommand(); q.Transaction = tx;
        q.CommandText = @"SELECT COUNT(*) FROM monarch_users u
JOIN monarch_user_company_access ca ON ca.user_id=u.id AND ca.company_id=$co AND ca.is_active=1
JOIN monarch_user_branch_access ba ON ba.user_id=u.id AND ba.company_id=$co AND ba.branch_id=$br AND ba.is_active=1
JOIN monarch_branches b ON b.company_id=ba.company_id AND b.id=ba.branch_id AND b.is_active=1
JOIN monarch_user_roles ur ON ur.user_id=u.id
JOIN monarch_role_permissions rp ON rp.role_id=ur.role_id AND rp.permission_code='reports.read'
WHERE u.id=$user AND u.is_active=1
AND EXISTS(SELECT 1 FROM monarch_companies co WHERE co.id=$co AND co.is_active=1);";
        q.Parameters.AddWithValue("$co", company.ToString());
        q.Parameters.AddWithValue("$br", branch.ToString());
        q.Parameters.AddWithValue("$user", user.ToString());
        if (Convert.ToInt64(q.ExecuteScalar()) < 1)
            throw new UnauthorizedAccessException("reports.read plus active company and branch access are required.");
    }
    private SqliteConnection Open(){var c=new SqliteConnection(_connectionString);c.Open();using var p=c.CreateCommand();p.CommandText="PRAGMA foreign_keys=ON;";p.ExecuteNonQuery();return c;}
}
