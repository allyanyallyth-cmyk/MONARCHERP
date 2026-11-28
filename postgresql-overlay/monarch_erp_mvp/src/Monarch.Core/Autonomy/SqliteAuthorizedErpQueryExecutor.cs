using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;

namespace Monarch.Core.Autonomy;

/// <summary>
/// Executes the explicitly supported read-only conversational intents. Authorization and tenant
/// predicates are evaluated in the same SQLite transaction as the query. Unsupported intents fail closed.
/// </summary>
public sealed class SqliteAuthorizedErpQueryExecutor : IAuthorizedErpQueryExecutor
{
    private readonly string _connectionString;
    public SqliteAuthorizedErpQueryExecutor(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath)) throw new ArgumentException("Database path is required.", nameof(databasePath));
        var full = Path.GetFullPath(databasePath);
        var directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = full, ForeignKeys = true, Mode = SqliteOpenMode.ReadWrite }.ToString();
    }

    public ErpQueryResult Execute(ErpUserScope scope, ErpQueryRequest request)
    {
        if (scope == null || request == null || scope.UserId == Guid.Empty || scope.CompanyId == Guid.Empty || scope.BranchId == Guid.Empty)
            throw new UnauthorizedAccessException("Authenticated tenant scope is required.");
        if (request.CompanyId != scope.CompanyId || request.BranchId != scope.BranchId)
            throw new UnauthorizedAccessException("Requested tenant scope does not match the authenticated scope.");
        if (request.IntentCode != "ZatcaPendingInvoices" || request.RequiredPermission != "reports.read")
            throw new NotSupportedException("This intent is not yet backed by a tenant-safe SQLite query. No query was executed.");

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using (var pragma = connection.CreateCommand()) { pragma.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;"; pragma.ExecuteNonQuery(); }
        using var transaction = connection.BeginTransaction();
        RequirePermission(connection, transaction, scope, request.RequiredPermission);

        var rows = new List<IReadOnlyDictionary<string, object>>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = @"SELECT c.invoice_number,c.invoice_kind,c.submission_status,o.attempts,o.created_utc
FROM monarch_zatca_submission_outbox o
JOIN monarch_zatca_invoice_chain c ON c.company_id=o.company_id AND c.id=o.chain_record_id
WHERE o.company_id=$company AND o.branch_id=$branch
AND o.submission_status IN ('Pending','Processing','Failed')
ORDER BY o.created_utc DESC LIMIT 500;";
            command.Parameters.AddWithValue("$company", scope.CompanyId.ToString());
            command.Parameters.AddWithValue("$branch", scope.BranchId.ToString());
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var row = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["invoiceNumber"] = reader.GetString(0), ["invoiceKind"] = reader.GetString(1),
                    ["submissionStatus"] = reader.GetString(2), ["attempts"] = reader.GetInt32(3),
                    ["createdUtc"] = reader.GetString(4)
                };
                rows.Add(row);
            }
        }
        transaction.Commit();
        return new ErpQueryResult(request.IntentCode, "Success", "فواتير ZATCA غير المكتملة", rows,
            rows.Count == 0 ? "لا توجد فواتير معلقة ضمن نطاق المنشأة والفرع الحاليين." : null);
    }

    private static void RequirePermission(SqliteConnection connection, SqliteTransaction transaction, ErpUserScope scope, string permission)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = @"SELECT COUNT(*) FROM monarch_users u
JOIN monarch_user_company_access ca ON ca.user_id=u.id AND ca.company_id=$company AND ca.is_active=1
JOIN monarch_user_branch_access ba ON ba.user_id=u.id AND ba.company_id=$company AND ba.branch_id=$branch AND ba.is_active=1
JOIN monarch_user_roles ur ON ur.user_id=u.id
JOIN monarch_role_permissions rp ON rp.role_id=ur.role_id AND rp.permission_code=$permission
WHERE u.id=$user AND u.is_active=1
AND EXISTS(SELECT 1 FROM monarch_companies co WHERE co.id=$company AND co.is_active=1)
AND EXISTS(SELECT 1 FROM monarch_branches b WHERE b.company_id=$company AND b.id=$branch AND b.is_active=1);";
        command.Parameters.AddWithValue("$company", scope.CompanyId.ToString());
        command.Parameters.AddWithValue("$branch", scope.BranchId.ToString());
        command.Parameters.AddWithValue("$user", scope.UserId.ToString());
        command.Parameters.AddWithValue("$permission", permission);
        if (Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) < 1)
            throw new UnauthorizedAccessException("User lacks reports.read permission in the requested company and branch.");
    }
}
