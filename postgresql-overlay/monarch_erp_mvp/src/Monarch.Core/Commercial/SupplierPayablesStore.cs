using Microsoft.Data.Sqlite;
using System.Globalization;

namespace Monarch.Core.Commercial;

public sealed record SupplierPayableOpenInvoice(Guid InvoiceId, string InvoiceNumber, DateOnly InvoiceDate,
    DateOnly DueDate, string CurrencyCode, int CurrencyScale, long OriginalMinor, long PaidMinor, long OutstandingMinor);
public sealed record SupplierPayableAllocation(Guid InvoiceId, long AmountMinor);
public sealed record SupplierPayablePaymentDraft(Guid Id, string Number, Guid SupplierId, DateOnly PaymentDate,
    CommercialPaymentMethod Method, string CashBankAccountCode, string CurrencyCode, int CurrencyScale,
    IReadOnlyList<SupplierPayableAllocation> Allocations);
public sealed record SupplierPayableAgingRow(Guid SupplierId, string SupplierCode, string SupplierName,
    Guid InvoiceId, string InvoiceNumber, DateOnly InvoiceDate, DateOnly DueDate, string CurrencyCode,
    int CurrencyScale, long OriginalMinor, long PaidMinor, long OutstandingMinor, int DaysOverdue,
    long CurrentMinor, long Days1To30Minor, long Days31To60Minor, long Days61To90Minor, long Over90Minor);

/// <summary>
/// Supplier AP subledger. Posted supplier invoices create one immutable payable balance;
/// payments are posted with exact allocations in one transaction. Accounting outbox events
/// are hand-offs only and are not themselves GL journals.
/// </summary>
public sealed class SupplierPayablesStore
{
    private readonly string _connectionString;
    public SupplierPayablesStore(string databasePath)
    {
        var full = Path.GetFullPath(databasePath); Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = full, ForeignKeys = true }.ToString();
    }

    public IReadOnlyList<SupplierPayableOpenInvoice> ListOpenInvoices(Guid userId, Guid companyId, Guid branchId,
        Guid supplierId, string? currencyCode = null)
    {
        using var c = Open(); using var tx = c.BeginTransaction(); Require(c, tx, userId, companyId, branchId, "reports.read");
        using var q = c.CreateCommand(); q.Transaction = tx;
        q.CommandText = @"SELECT ap.invoice_id,ap.invoice_number,ap.invoice_date,ap.due_date,ap.currency_code,ap.currency_scale,ap.original_minor,
COALESCE(SUM(a.amount_minor),0) paid_minor,ap.original_minor-COALESCE(SUM(a.amount_minor),0) outstanding_minor
FROM monarch_supplier_payables ap LEFT JOIN monarch_supplier_payment_allocations a ON a.company_id=ap.company_id AND a.invoice_id=ap.invoice_id
WHERE ap.company_id=$co AND ap.branch_id=$br AND ap.supplier_id=$supplier AND ap.status<>'Paid'
AND ($currency IS NULL OR ap.currency_code=$currency)
GROUP BY ap.company_id,ap.invoice_id HAVING outstanding_minor>0 ORDER BY ap.due_date,ap.invoice_date,ap.invoice_number;";
        q.Parameters.AddWithValue("$co", companyId.ToString()); q.Parameters.AddWithValue("$br", branchId.ToString());
        q.Parameters.AddWithValue("$supplier", supplierId.ToString()); q.Parameters.AddWithValue("$currency", (object?)currencyCode?.ToUpperInvariant() ?? DBNull.Value);
        var rows = new List<SupplierPayableOpenInvoice>(); using var r = q.ExecuteReader();
        while (r.Read()) rows.Add(new(Guid.Parse(r.GetString(0)), r.GetString(1), DateOnly.ParseExact(r.GetString(2), "yyyy-MM-dd", CultureInfo.InvariantCulture),
            DateOnly.ParseExact(r.GetString(3), "yyyy-MM-dd", CultureInfo.InvariantCulture), r.GetString(4), r.GetInt32(5), r.GetInt64(6), r.GetInt64(7), r.GetInt64(8)));
        tx.Commit(); return rows;
    }

    public void PostPayment(Guid userId, Guid companyId, Guid branchId, SupplierPayablePaymentDraft payment)
    {
        if (payment.Id == Guid.Empty || payment.SupplierId == Guid.Empty || string.IsNullOrWhiteSpace(payment.Number) ||
            payment.Number.Trim().Length > 60 || string.IsNullOrWhiteSpace(payment.CashBankAccountCode) ||
            payment.Allocations is null || payment.Allocations.Count is < 1 or > 500)
            throw new ArgumentException("بيانات الدفعة والمورد والحساب وتخصيص فاتورة واحدة على الأقل مطلوبة.");
        if (payment.CurrencyCode is null || payment.CurrencyCode.Length != 3 || payment.CurrencyScale is < 0 or > 4)
            throw new ArgumentException("رمز العملة أو دقتها غير صحيح.");
        if (payment.Allocations.Any(x => x.InvoiceId == Guid.Empty || x.AmountMinor <= 0) ||
            payment.Allocations.Select(x => x.InvoiceId).Distinct().Count() != payment.Allocations.Count)
            throw new ArgumentException("تخصيصات الدفعة مكررة أو تحتوي مبلغاً غير صحيح.");
        long amount = 0; foreach (var a in payment.Allocations) amount = checked(amount + a.AmountMinor);

        using var c = Open(); using var tx = c.BeginTransaction(); Require(c, tx, userId, companyId, branchId, "commercial.purchases.manage");
        using (var party = c.CreateCommand())
        {
            party.Transaction = tx; party.CommandText = "SELECT 1 FROM monarch_parties WHERE company_id=$co AND id=$supplier AND is_active=1 AND party_type IN('Supplier','Both');";
            party.Parameters.AddWithValue("$co", companyId.ToString()); party.Parameters.AddWithValue("$supplier", payment.SupplierId.ToString());
            if (party.ExecuteScalar() is null) throw new InvalidOperationException("المورد غير موجود أو غير نشط أو لا يحمل صفة مورد.");
        }
        using (var insert = c.CreateCommand())
        {
            insert.Transaction = tx; insert.CommandText = @"INSERT INTO monarch_supplier_payments(id,company_id,branch_id,supplier_id,payment_number,payment_date,payment_method,cash_bank_account_code,currency_code,currency_scale,amount_minor,status,created_utc,created_by)
VALUES($id,$co,$br,$supplier,$number,$date,$method,$account,$currency,$scale,$amount,'Posted',$now,$user);";
            insert.Parameters.AddWithValue("$id", payment.Id.ToString()); insert.Parameters.AddWithValue("$co", companyId.ToString()); insert.Parameters.AddWithValue("$br", branchId.ToString());
            insert.Parameters.AddWithValue("$supplier", payment.SupplierId.ToString()); insert.Parameters.AddWithValue("$number", payment.Number.Trim());
            insert.Parameters.AddWithValue("$date", payment.PaymentDate.ToString("yyyy-MM-dd")); insert.Parameters.AddWithValue("$method", payment.Method.ToString());
            insert.Parameters.AddWithValue("$account", payment.CashBankAccountCode.Trim()); insert.Parameters.AddWithValue("$currency", payment.CurrencyCode.ToUpperInvariant());
            insert.Parameters.AddWithValue("$scale", payment.CurrencyScale); insert.Parameters.AddWithValue("$amount", amount);
            insert.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O")); insert.Parameters.AddWithValue("$user", userId.ToString()); insert.ExecuteNonQuery();
        }
        foreach (var allocation in payment.Allocations)
        {
            using (var validate = c.CreateCommand())
            {
                validate.Transaction = tx; validate.CommandText = @"SELECT ap.supplier_id,ap.branch_id,ap.currency_code,ap.currency_scale,ap.original_minor,
COALESCE((SELECT SUM(x.amount_minor) FROM monarch_supplier_payment_allocations x WHERE x.company_id=ap.company_id AND x.invoice_id=ap.invoice_id),0)
FROM monarch_supplier_payables ap JOIN monarch_supplier_invoices i ON i.company_id=ap.company_id AND i.id=ap.invoice_id
WHERE ap.company_id=$co AND ap.invoice_id=$invoice AND i.status='Posted';";
                validate.Parameters.AddWithValue("$co", companyId.ToString()); validate.Parameters.AddWithValue("$invoice", allocation.InvoiceId.ToString());
                using var r = validate.ExecuteReader();
                if (!r.Read()) throw new InvalidOperationException("فاتورة الذمم غير موجودة أو غير مرحّلة.");
                if (r.GetString(0) != payment.SupplierId.ToString() || r.GetString(1) != branchId.ToString() ||
                    r.GetString(2) != payment.CurrencyCode.ToUpperInvariant() || r.GetInt32(3) != payment.CurrencyScale)
                    throw new InvalidOperationException("المورد أو الفرع أو العملة لا تتطابق مع الفاتورة.");
                var outstanding = checked(r.GetInt64(4) - r.GetInt64(5)); r.Close();
                if (allocation.AmountMinor > outstanding) throw new InvalidOperationException("مبلغ التخصيص يتجاوز الرصيد المستحق للفاتورة.");
            }
            using var add = c.CreateCommand(); add.Transaction = tx;
            add.CommandText = "INSERT INTO monarch_supplier_payment_allocations(id,company_id,payment_id,invoice_id,amount_minor) VALUES($id,$co,$payment,$invoice,$amount);";
            add.Parameters.AddWithValue("$id", Guid.NewGuid().ToString()); add.Parameters.AddWithValue("$co", companyId.ToString());
            add.Parameters.AddWithValue("$payment", payment.Id.ToString()); add.Parameters.AddWithValue("$invoice", allocation.InvoiceId.ToString());
            add.Parameters.AddWithValue("$amount", allocation.AmountMinor); add.ExecuteNonQuery();
        }
        using (var reconcile = c.CreateCommand())
        {
            reconcile.Transaction = tx; reconcile.CommandText = "SELECT COALESCE(SUM(amount_minor),0) FROM monarch_supplier_payment_allocations WHERE company_id=$co AND payment_id=$id;";
            reconcile.Parameters.AddWithValue("$co", companyId.ToString()); reconcile.Parameters.AddWithValue("$id", payment.Id.ToString());
            if (Convert.ToInt64(reconcile.ExecuteScalar()) != amount) throw new InvalidOperationException("مجموع تخصيصات الدفعة لا يساوي قيمتها.");
        }
        using (var update = c.CreateCommand())
        {
            update.Transaction = tx; update.CommandText = @"UPDATE monarch_supplier_payables SET status=CASE WHEN original_minor<=
(SELECT COALESCE(SUM(amount_minor),0) FROM monarch_supplier_payment_allocations WHERE company_id=$co AND invoice_id=monarch_supplier_payables.invoice_id) THEN 'Paid' ELSE 'PartiallyPaid' END
WHERE company_id=$co AND invoice_id IN(SELECT invoice_id FROM monarch_supplier_payment_allocations WHERE company_id=$co AND payment_id=$payment);";
            update.Parameters.AddWithValue("$co", companyId.ToString()); update.Parameters.AddWithValue("$payment", payment.Id.ToString()); update.ExecuteNonQuery();
        }
        using (var outbox = c.CreateCommand())
        {
            outbox.Transaction = tx; outbox.CommandText = "INSERT INTO monarch_supplier_payment_accounting_outbox(id,company_id,branch_id,payment_id,event_type,created_utc) VALUES($id,$co,$br,$payment,'SupplierPaymentPosted',$now);";
            outbox.Parameters.AddWithValue("$id", Guid.NewGuid().ToString()); outbox.Parameters.AddWithValue("$co", companyId.ToString());
            outbox.Parameters.AddWithValue("$br", branchId.ToString()); outbox.Parameters.AddWithValue("$payment", payment.Id.ToString());
            outbox.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O")); outbox.ExecuteNonQuery();
        }
        using (var audit = c.CreateCommand())
        {
            audit.Transaction = tx; audit.CommandText = "INSERT INTO monarch_company_audit_log(company_id,branch_id,actor_user_id,action,entity_type,entity_id,occurred_utc) VALUES($co,$br,$user,'SupplierPaymentPosted','SupplierPayment',$id,$now);";
            audit.Parameters.AddWithValue("$co", companyId.ToString()); audit.Parameters.AddWithValue("$br", branchId.ToString());
            audit.Parameters.AddWithValue("$user", userId.ToString()); audit.Parameters.AddWithValue("$id", payment.Id.ToString());
            audit.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O")); audit.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public IReadOnlyList<SupplierPayableAgingRow> GetAging(Guid userId, Guid companyId, Guid branchId, DateOnly asOfDate, Guid? supplierId = null)
    {
        using var c = Open(); using var tx = c.BeginTransaction(); Require(c, tx, userId, companyId, branchId, "reports.read");
        using var q = c.CreateCommand(); q.Transaction = tx;
        q.CommandText = @"WITH balances AS (
SELECT ap.supplier_id,p.code,p.display_name,ap.invoice_id,ap.invoice_number,ap.invoice_date,ap.due_date,ap.currency_code,ap.currency_scale,ap.original_minor,
COALESCE(SUM(CASE WHEN pay.id IS NOT NULL THEN a.amount_minor ELSE 0 END),0) paid_minor,
ap.original_minor-COALESCE(SUM(CASE WHEN pay.id IS NOT NULL THEN a.amount_minor ELSE 0 END),0) outstanding_minor,
CAST(julianday(date($asof))-julianday(ap.due_date) AS INTEGER) overdue_days
FROM monarch_supplier_payables ap JOIN monarch_parties p ON p.company_id=ap.company_id AND p.id=ap.supplier_id
LEFT JOIN monarch_supplier_payment_allocations a ON a.company_id=ap.company_id AND a.invoice_id=ap.invoice_id
LEFT JOIN monarch_supplier_payments pay ON pay.company_id=a.company_id AND pay.id=a.payment_id
 AND pay.branch_id=ap.branch_id AND pay.status='Posted' AND date(pay.payment_date)<=date($asof)
WHERE ap.company_id=$co AND ap.branch_id=$br AND date(ap.invoice_date)<=date($asof) AND ($supplier IS NULL OR ap.supplier_id=$supplier)
GROUP BY ap.company_id,ap.invoice_id HAVING outstanding_minor>0)
SELECT supplier_id,code,display_name,invoice_id,invoice_number,invoice_date,due_date,currency_code,currency_scale,original_minor,paid_minor,outstanding_minor,overdue_days,
CASE WHEN overdue_days<=0 THEN outstanding_minor ELSE 0 END,CASE WHEN overdue_days BETWEEN 1 AND 30 THEN outstanding_minor ELSE 0 END,
CASE WHEN overdue_days BETWEEN 31 AND 60 THEN outstanding_minor ELSE 0 END,CASE WHEN overdue_days BETWEEN 61 AND 90 THEN outstanding_minor ELSE 0 END,
CASE WHEN overdue_days>90 THEN outstanding_minor ELSE 0 END FROM balances ORDER BY code,due_date,invoice_number;";
        q.Parameters.AddWithValue("$co", companyId.ToString()); q.Parameters.AddWithValue("$br", branchId.ToString()); q.Parameters.AddWithValue("$asof", asOfDate.ToString("yyyy-MM-dd"));
        q.Parameters.AddWithValue("$supplier", (object?)supplierId?.ToString() ?? DBNull.Value);
        var rows = new List<SupplierPayableAgingRow>(); using var r = q.ExecuteReader();
        while (r.Read()) rows.Add(new(Guid.Parse(r.GetString(0)), r.GetString(1), r.GetString(2), Guid.Parse(r.GetString(3)), r.GetString(4),
            DateOnly.ParseExact(r.GetString(5), "yyyy-MM-dd", CultureInfo.InvariantCulture), DateOnly.ParseExact(r.GetString(6), "yyyy-MM-dd", CultureInfo.InvariantCulture),
            r.GetString(7), r.GetInt32(8), r.GetInt64(9), r.GetInt64(10), r.GetInt64(11), r.GetInt32(12), r.GetInt64(13), r.GetInt64(14), r.GetInt64(15), r.GetInt64(16), r.GetInt64(17)));
        tx.Commit(); return rows;
    }

    private static void Require(SqliteConnection c, SqliteTransaction tx, Guid user, Guid co, Guid br, string permission)
    {
        using var q = c.CreateCommand(); q.Transaction = tx;
        q.CommandText = @"SELECT COUNT(*) FROM monarch_users u JOIN monarch_user_company_access ca ON ca.user_id=u.id AND ca.company_id=$co AND ca.is_active=1
JOIN monarch_user_branch_access ba ON ba.user_id=u.id AND ba.company_id=$co AND ba.branch_id=$br AND ba.is_active=1
JOIN monarch_branches b ON b.company_id=ba.company_id AND b.id=ba.branch_id AND b.is_active=1
JOIN monarch_user_roles ur ON ur.user_id=u.id JOIN monarch_role_permissions rp ON rp.role_id=ur.role_id AND rp.permission_code=$permission
WHERE u.id=$user AND u.is_active=1 AND EXISTS(SELECT 1 FROM monarch_companies co WHERE co.id=$co AND co.is_active=1);";
        q.Parameters.AddWithValue("$co", co.ToString()); q.Parameters.AddWithValue("$br", br.ToString()); q.Parameters.AddWithValue("$user", user.ToString()); q.Parameters.AddWithValue("$permission", permission);
        if (Convert.ToInt64(q.ExecuteScalar()) < 1) throw new UnauthorizedAccessException("المستخدم لا يملك صلاحية العملية ضمن المنشأة والفرع المحددين.");
    }
    private SqliteConnection Open() { var c = new SqliteConnection(_connectionString); c.Open(); using var p = c.CreateCommand(); p.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;"; p.ExecuteNonQuery(); return c; }
}
