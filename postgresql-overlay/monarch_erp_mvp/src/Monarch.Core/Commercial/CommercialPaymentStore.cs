using Microsoft.Data.Sqlite;

namespace Monarch.Core.Commercial;

public enum CommercialPaymentType { CustomerReceipt, SupplierPayment }
public enum CommercialPaymentMethod { Cash, Bank }
public sealed record CommercialPaymentAllocation(Guid DocumentId, long AmountMinor);
public sealed record CommercialPaymentDraft(Guid Id, string Number, CommercialPaymentType Type, Guid PartyId,
    CommercialPaymentMethod Method, string CashBankAccountCode, string CurrencyCode, int CurrencyScale,
    DateOnly PaymentDate, IReadOnlyList<CommercialPaymentAllocation> Allocations);

/// <summary>
/// Posts fully allocated customer receipts and supplier payments. Payment, allocations,
/// and the GL outbox event commit atomically. GL posting is performed by a separate worker.
/// </summary>
public sealed class CommercialPaymentStore
{
    private readonly string _connectionString;
    public CommercialPaymentStore(string databasePath)
    {
        var full = Path.GetFullPath(databasePath); Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = full, ForeignKeys = true }.ToString();
    }

    public void Post(Guid userId, Guid companyId, Guid branchId, CommercialPaymentDraft payment)
    {
        if (!Enum.IsDefined(typeof(CommercialPaymentType), payment.Type) ||
            !Enum.IsDefined(typeof(CommercialPaymentMethod), payment.Method))
            throw new ArgumentOutOfRangeException(nameof(payment), "Unknown payment type or method.");
        if (payment.Id == Guid.Empty || string.IsNullOrWhiteSpace(payment.Number) ||
            string.IsNullOrWhiteSpace(payment.CashBankAccountCode) || payment.Allocations is null || payment.Allocations.Count == 0)
            throw new ArgumentException("Payment identity, number, account and allocations are required.");
        if (payment.CurrencyCode is null || payment.CurrencyCode.Length != 3 || payment.CurrencyScale is < 0 or > 4)
            throw new ArgumentException("Invalid currency metadata.");
        if (payment.Allocations.Any(x => x.DocumentId == Guid.Empty || x.AmountMinor <= 0) ||
            payment.Allocations.Select(x => x.DocumentId).Distinct().Count() != payment.Allocations.Count)
            throw new ArgumentException("Each allocation must reference a unique document and positive amount.");
        long amount = 0;
        foreach (var allocation in payment.Allocations) amount = checked(amount + allocation.AmountMinor);

        using var c = Open(); using var tx = c.BeginTransaction();
        RequirePermission(c, tx, userId, companyId, branchId,
            payment.Type == CommercialPaymentType.CustomerReceipt ? "commercial.sales.manage" : "commercial.purchases.manage");
        var expectedPartyType = payment.Type == CommercialPaymentType.CustomerReceipt ? "Customer" : "Supplier";
        using (var party = c.CreateCommand())
        {
            party.Transaction = tx; party.CommandText = "SELECT party_type,is_active FROM monarch_parties WHERE company_id=$co AND id=$id;";
            party.Parameters.AddWithValue("$co", companyId.ToString()); party.Parameters.AddWithValue("$id", payment.PartyId.ToString());
            using var r = party.ExecuteReader();
            if (!r.Read() || r.GetInt64(1) != 1 || (r.GetString(0) != "Both" && r.GetString(0) != expectedPartyType))
                throw new InvalidOperationException("Payment party is inactive or has the wrong customer/supplier role.");
        }
        using (var insert = c.CreateCommand())
        {
            insert.Transaction = tx;
            insert.CommandText = @"INSERT INTO monarch_commercial_payments(id,company_id,branch_id,payment_type,payment_number,party_id,payment_method,cash_bank_account_code,currency_code,currency_scale,amount_minor,payment_date,status,created_utc,created_by)
VALUES($id,$co,$br,$type,$number,$party,$method,$account,$currency,$scale,$amount,$date,'Posted',$now,$user);";
            insert.Parameters.AddWithValue("$id", payment.Id.ToString()); insert.Parameters.AddWithValue("$co", companyId.ToString());
            insert.Parameters.AddWithValue("$br", branchId.ToString()); insert.Parameters.AddWithValue("$type", payment.Type.ToString());
            insert.Parameters.AddWithValue("$number", payment.Number.Trim()); insert.Parameters.AddWithValue("$party", payment.PartyId.ToString());
            insert.Parameters.AddWithValue("$method", payment.Method.ToString()); insert.Parameters.AddWithValue("$account", payment.CashBankAccountCode.Trim());
            insert.Parameters.AddWithValue("$currency", payment.CurrencyCode.ToUpperInvariant()); insert.Parameters.AddWithValue("$scale", payment.CurrencyScale);
            insert.Parameters.AddWithValue("$amount", amount); insert.Parameters.AddWithValue("$date", payment.PaymentDate.ToString("yyyy-MM-dd"));
            insert.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O")); insert.Parameters.AddWithValue("$user", userId.ToString()); insert.ExecuteNonQuery();
        }

        foreach (var allocation in payment.Allocations)
        {
            using (var document = c.CreateCommand())
            {
                document.Transaction = tx;
                document.CommandText = @"SELECT document_type,party_id,currency_code,currency_scale,total_minor,status,branch_id
FROM monarch_commercial_documents WHERE company_id=$co AND id=$id;";
                document.Parameters.AddWithValue("$co", companyId.ToString()); document.Parameters.AddWithValue("$id", allocation.DocumentId.ToString());
                using var r = document.ExecuteReader();
                if (!r.Read()) throw new InvalidOperationException("Allocated document was not found in this company.");
                var expectedDocumentType = payment.Type == CommercialPaymentType.CustomerReceipt ? "Sale" : "Purchase";
                if (r.GetString(0) != expectedDocumentType || r.GetString(1) != payment.PartyId.ToString() ||
                    r.GetString(2) != payment.CurrencyCode.ToUpperInvariant() || r.GetInt32(3) != payment.CurrencyScale || r.GetString(5) != "Posted")
                    throw new InvalidOperationException("Allocation document type, party, currency or status does not match the payment.");
                if (r.GetString(6) != branchId.ToString()) throw new UnauthorizedAccessException("Payment allocation must target a document in the selected branch.");
                long total = r.GetInt64(4);
                r.Close();
                using var outstanding = c.CreateCommand(); outstanding.Transaction = tx;
                outstanding.CommandText = @"SELECT COALESCE(SUM(a.amount_minor),0) FROM monarch_commercial_payment_allocations a
JOIN monarch_commercial_payments p ON p.company_id=a.company_id AND p.id=a.payment_id
WHERE a.company_id=$co AND a.document_id=$doc AND p.status='Posted';";
                outstanding.Parameters.AddWithValue("$co", companyId.ToString()); outstanding.Parameters.AddWithValue("$doc", allocation.DocumentId.ToString());
                var paid = Convert.ToInt64(outstanding.ExecuteScalar());
                if (allocation.AmountMinor > total - paid) throw new InvalidOperationException("Allocation exceeds the document outstanding balance.");
            }
            using var alloc = c.CreateCommand(); alloc.Transaction = tx;
            alloc.CommandText = "INSERT INTO monarch_commercial_payment_allocations(id,company_id,payment_id,document_id,amount_minor) VALUES($id,$co,$payment,$doc,$amount);";
            alloc.Parameters.AddWithValue("$id", Guid.NewGuid().ToString()); alloc.Parameters.AddWithValue("$co", companyId.ToString());
            alloc.Parameters.AddWithValue("$payment", payment.Id.ToString()); alloc.Parameters.AddWithValue("$doc", allocation.DocumentId.ToString());
            alloc.Parameters.AddWithValue("$amount", allocation.AmountMinor); alloc.ExecuteNonQuery();
        }
        using (var sum = c.CreateCommand())
        {
            sum.Transaction = tx; sum.CommandText = "SELECT COALESCE(SUM(amount_minor),0) FROM monarch_commercial_payment_allocations WHERE company_id=$co AND payment_id=$id;";
            sum.Parameters.AddWithValue("$co", companyId.ToString()); sum.Parameters.AddWithValue("$id", payment.Id.ToString());
            if (Convert.ToInt64(sum.ExecuteScalar()) != amount) throw new InvalidOperationException("Payment allocations do not reconcile to the payment amount.");
        }
        using (var outbox = c.CreateCommand())
        {
            outbox.Transaction = tx; outbox.CommandText = @"INSERT INTO monarch_payment_accounting_outbox(id,company_id,branch_id,payment_id,event_type,created_utc)
VALUES($id,$co,$br,$payment,$event,$now);";
            outbox.Parameters.AddWithValue("$id", Guid.NewGuid().ToString()); outbox.Parameters.AddWithValue("$co", companyId.ToString());
            outbox.Parameters.AddWithValue("$br", branchId.ToString()); outbox.Parameters.AddWithValue("$payment", payment.Id.ToString());
            outbox.Parameters.AddWithValue("$event", payment.Type == CommercialPaymentType.CustomerReceipt ? "CustomerReceiptPosted" : "SupplierPaymentPosted");
            outbox.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O")); outbox.ExecuteNonQuery();
        }
        Audit(c, tx, userId, companyId, branchId,
            payment.Type == CommercialPaymentType.CustomerReceipt ? "CustomerReceiptPosted" : "SupplierPaymentPosted",
            payment.Id,
            "party=" + payment.PartyId.ToString("D") +
            ";type=" + payment.Type +
            ";amountMinor=" + amount.ToString(System.Globalization.CultureInfo.InvariantCulture) +
            ";currency=" + payment.CurrencyCode.ToUpperInvariant() +
            ";method=" + payment.Method +
            ";account=" + payment.CashBankAccountCode.Trim() +
            ";allocations=" + payment.Allocations.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        tx.Commit();
    }

    // Enforce tenant membership and granular permission inside the payment transaction.
    // This closes the access-revocation race and prevents role-name based authorization.
    private static void RequirePermission(SqliteConnection c, SqliteTransaction tx,
        Guid user, Guid company, Guid branch, string permission)
    {
        using var q = c.CreateCommand(); q.Transaction = tx;
        q.CommandText = @"SELECT COUNT(*) FROM monarch_users u
JOIN monarch_user_company_access ca ON ca.user_id=u.id AND ca.company_id=$co AND ca.is_active=1
JOIN monarch_user_branch_access ba ON ba.user_id=u.id AND ba.company_id=$co AND ba.branch_id=$br AND ba.is_active=1
JOIN monarch_branches b ON b.company_id=ba.company_id AND b.id=ba.branch_id AND b.is_active=1
JOIN monarch_user_roles ur ON ur.user_id=u.id
JOIN monarch_role_permissions rp ON rp.role_id=ur.role_id AND rp.permission_code=$permission
WHERE u.id=$user AND u.is_active=1
AND EXISTS(SELECT 1 FROM monarch_companies co WHERE co.id=$co AND co.is_active=1);";
        q.Parameters.AddWithValue("$co", company.ToString());
        q.Parameters.AddWithValue("$br", branch.ToString());
        q.Parameters.AddWithValue("$user", user.ToString());
        q.Parameters.AddWithValue("$permission", permission);
        if (Convert.ToInt64(q.ExecuteScalar()) < 1)
            throw new UnauthorizedAccessException("المستخدم لا يملك صلاحية ترحيل سند الدفع ضمن المنشأة والفرع المحددين.");
    }

    private static void Audit(SqliteConnection c, SqliteTransaction tx, Guid user,
        Guid company, Guid branch, string action, Guid paymentId, string details)
    {
        using var audit = c.CreateCommand(); audit.Transaction = tx;
        audit.CommandText = @"INSERT INTO monarch_company_audit_log
(company_id,branch_id,actor_user_id,action,entity_type,entity_id,details,occurred_utc)
VALUES($co,$br,$user,$action,'CommercialPayment',$id,$details,$now);";
        audit.Parameters.AddWithValue("$co", company.ToString());
        audit.Parameters.AddWithValue("$br", branch.ToString());
        audit.Parameters.AddWithValue("$user", user.ToString());
        audit.Parameters.AddWithValue("$action", action);
        audit.Parameters.AddWithValue("$id", paymentId.ToString());
        audit.Parameters.AddWithValue("$details", details);
        audit.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        audit.ExecuteNonQuery();
    }
    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString); c.Open();
        using var p = c.CreateCommand(); p.CommandText = "PRAGMA foreign_keys=ON;"; p.ExecuteNonQuery(); return c;
    }
}
