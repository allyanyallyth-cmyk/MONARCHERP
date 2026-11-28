using Microsoft.Data.Sqlite;

namespace Monarch.Core.Organizations;

/// <summary>Offline company, branch and user-access directory for tenant-scoped modules.</summary>
public sealed class CompanyDirectoryStore
{
    private readonly string _connectionString;

    public CompanyDirectoryStore(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath)) throw new ArgumentException("Database path is required.", nameof(databasePath));
        var fullPath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource=fullPath, ForeignKeys=true }.ToString();
    }

    public void Initialize(string? schemaPath = null)
    {
        schemaPath ??= Path.Combine(AppContext.BaseDirectory, "database", "tenancy.sql");
        if (!File.Exists(schemaPath)) throw new FileNotFoundException("MONARCH organization schema was not found.", schemaPath);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = File.ReadAllText(schemaPath);
        command.ExecuteNonQuery();
    }

    /// <summary>Creates a company, its head office, and the creator's administrator access atomically.</summary>
    public Guid CreateCompanyForAdministrator(string code, string legalName, string displayName,
        string countryCode, string baseCurrencyCode, string timeZoneId, Guid administratorUserId)
    {
        Validate(code, legalName, displayName, countryCode, baseCurrencyCode, timeZoneId);
        if (administratorUserId == Guid.Empty) throw new ArgumentException("Administrator user is required.", nameof(administratorUserId));
        var companyId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow.ToString("O");
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using (var user = connection.CreateCommand())
        {
            user.Transaction = transaction;
            user.CommandText = @"SELECT COUNT(*) FROM monarch_users u
JOIN monarch_user_roles ur ON ur.user_id=u.id
JOIN monarch_roles r ON r.id=ur.role_id AND r.code='Administrator' COLLATE NOCASE
WHERE u.id=$user AND u.is_active=1;";
            user.Parameters.AddWithValue("$user", administratorUserId.ToString());
            if (Convert.ToInt64(user.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 1)
                throw new UnauthorizedAccessException("An active administrator account is required.");
        }
        using (var company = connection.CreateCommand())
        {
            company.Transaction = transaction;
            company.CommandText = @"INSERT INTO monarch_companies
(id,code,legal_name,display_name,country_code,base_currency_code,time_zone_id,is_active,created_utc)
VALUES($id,$code,$legal,$display,$country,$currency,$zone,1,$created);";
            company.Parameters.AddWithValue("$id", companyId.ToString());
            company.Parameters.AddWithValue("$code", code.Trim());
            company.Parameters.AddWithValue("$legal", legalName.Trim());
            company.Parameters.AddWithValue("$display", displayName.Trim());
            company.Parameters.AddWithValue("$country", countryCode.Trim().ToUpperInvariant());
            company.Parameters.AddWithValue("$currency", baseCurrencyCode.Trim().ToUpperInvariant());
            company.Parameters.AddWithValue("$zone", timeZoneId.Trim());
            company.Parameters.AddWithValue("$created", now);
            company.ExecuteNonQuery();
        }
        using (var branch = connection.CreateCommand())
        {
            branch.Transaction = transaction;
            branch.CommandText = @"INSERT INTO monarch_branches(id,company_id,code,name,is_head_office,is_active,created_utc)
VALUES($id,$company,'HQ','المقر الرئيسي',1,1,$created);";
            branch.Parameters.AddWithValue("$id", branchId.ToString());
            branch.Parameters.AddWithValue("$company", companyId.ToString());
            branch.Parameters.AddWithValue("$created", now);
            branch.ExecuteNonQuery();
        }
        using (var access = connection.CreateCommand())
        {
            access.Transaction = transaction;
            access.CommandText = @"INSERT INTO monarch_user_company_access
(user_id,company_id,role_code,is_active,granted_by_user_id,granted_utc)
VALUES($user,$company,'Administrator',1,$actor,$now);";
            access.Parameters.AddWithValue("$user", administratorUserId.ToString());
            access.Parameters.AddWithValue("$company", companyId.ToString());
            access.Parameters.AddWithValue("$actor", administratorUserId.ToString());
            access.Parameters.AddWithValue("$now", now);
            access.ExecuteNonQuery();
        }
        using (var branchAccess = connection.CreateCommand())
        {
            branchAccess.Transaction = transaction;
            branchAccess.CommandText = @"INSERT INTO monarch_user_branch_access(user_id,company_id,branch_id,is_active)
VALUES($user,$company,$branch,1);";
            branchAccess.Parameters.AddWithValue("$user", administratorUserId.ToString());
            branchAccess.Parameters.AddWithValue("$company", companyId.ToString());
            branchAccess.Parameters.AddWithValue("$branch", branchId.ToString());
            branchAccess.ExecuteNonQuery();
        }
        using (var audit = connection.CreateCommand())
        {
            audit.Transaction = transaction;
            audit.CommandText = @"INSERT INTO monarch_company_audit_log
(company_id,branch_id,actor_user_id,action,entity_type,entity_id,details,occurred_utc)
VALUES($company,$branch,$actor,'CompanyCreated','Company',$company,$details,$now);";
            audit.Parameters.AddWithValue("$company", companyId.ToString());
            audit.Parameters.AddWithValue("$branch", branchId.ToString());
            audit.Parameters.AddWithValue("$actor", administratorUserId.ToString());
            audit.Parameters.AddWithValue("$details", "code=" + code.Trim());
            audit.Parameters.AddWithValue("$now", now);
            audit.ExecuteNonQuery();
        }
        transaction.Commit();
        return companyId;
    }

    /// <summary>Creates a branch only for an authenticated global administrator and records the action atomically.</summary>
    public Guid CreateBranch(Guid companyId, string code, string name, Guid createdByUserId,
        string? address = null, string? phone = null)
    {
        if (companyId == Guid.Empty || createdByUserId == Guid.Empty)
            throw new ArgumentException("Company and administrator are required.");
        if (string.IsNullOrWhiteSpace(code) || code.Trim().Length > 32)
            throw new ArgumentException("Branch code must be 1-32 characters.", nameof(code));
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 120)
            throw new ArgumentException("Branch name must be 1-120 characters.", nameof(name));
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow.ToString("O");
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using (var authorize = connection.CreateCommand())
        {
            authorize.Transaction = transaction;
            authorize.CommandText = @"SELECT COUNT(*) FROM monarch_users u
JOIN monarch_user_roles ur ON ur.user_id=u.id
JOIN monarch_roles r ON r.id=ur.role_id AND r.code='Administrator' COLLATE NOCASE
JOIN monarch_companies c ON c.id=$company AND c.is_active=1
WHERE u.id=$actor AND u.is_active=1;";
            authorize.Parameters.AddWithValue("$actor", createdByUserId.ToString());
            authorize.Parameters.AddWithValue("$company", companyId.ToString());
            if (Convert.ToInt64(authorize.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 1)
                throw new UnauthorizedAccessException("An active global administrator and company are required.");
        }
        using (var branch = connection.CreateCommand())
        {
            branch.Transaction = transaction;
            branch.CommandText = @"INSERT INTO monarch_branches(id,company_id,code,name,address,phone,is_head_office,is_active,created_utc)
VALUES($id,$company,$code,$name,$address,$phone,0,1,$created);";
            branch.Parameters.AddWithValue("$id", id.ToString());
            branch.Parameters.AddWithValue("$company", companyId.ToString());
            branch.Parameters.AddWithValue("$code", code.Trim());
            branch.Parameters.AddWithValue("$name", name.Trim());
            branch.Parameters.AddWithValue("$address", (object?)address ?? DBNull.Value);
            branch.Parameters.AddWithValue("$phone", (object?)phone ?? DBNull.Value);
            branch.Parameters.AddWithValue("$created", now);
            branch.ExecuteNonQuery();
        }
        using (var audit = connection.CreateCommand())
        {
            audit.Transaction = transaction;
            audit.CommandText = @"INSERT INTO monarch_company_audit_log
(company_id,branch_id,actor_user_id,action,entity_type,entity_id,details,occurred_utc)
VALUES($company,$branch,$actor,'BranchCreated','Branch',$branch,$details,$now);";
            audit.Parameters.AddWithValue("$company", companyId.ToString());
            audit.Parameters.AddWithValue("$branch", id.ToString());
            audit.Parameters.AddWithValue("$actor", createdByUserId.ToString());
            audit.Parameters.AddWithValue("$details", "code=" + code.Trim());
            audit.Parameters.AddWithValue("$now", now);
            audit.ExecuteNonQuery();
        }
        transaction.Commit();
        return id;
    }

    /// <summary>Grants membership only when the actor is a global administrator. Self-bootstrap is limited to the seeded default company.</summary>
    public void GrantCompanyAccess(Guid userId, Guid companyId, string roleCode, Guid grantedByUserId)
    {
        if (userId == Guid.Empty || companyId == Guid.Empty || grantedByUserId == Guid.Empty)
            throw new ArgumentException("User, company and granting administrator are required.");
        if (!new[] { "Administrator", "Accountant", "Viewer" }.Contains(roleCode, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("Unknown company role.", nameof(roleCode));
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using (var validate = connection.CreateCommand())
        {
            validate.Transaction = transaction;
            validate.CommandText = @"SELECT COUNT(*) FROM monarch_users actor
JOIN monarch_user_roles ur ON ur.user_id=actor.id
JOIN monarch_roles r ON r.id=ur.role_id AND r.code='Administrator' COLLATE NOCASE
JOIN monarch_users target ON target.id=$user AND target.is_active=1
JOIN monarch_companies c ON c.id=$company AND c.is_active=1
WHERE actor.id=$actor AND actor.is_active=1
AND ($actor<>$user OR ($company='00000000-0000-0000-0000-000000000001'
 AND $role='Administrator' COLLATE NOCASE
 AND NOT EXISTS(SELECT 1 FROM monarch_user_company_access x WHERE x.user_id=$user AND x.is_active=1)));";
            validate.Parameters.AddWithValue("$user", userId.ToString());
            validate.Parameters.AddWithValue("$company", companyId.ToString());
            validate.Parameters.AddWithValue("$actor", grantedByUserId.ToString());
            validate.Parameters.AddWithValue("$role", roleCode);
            if (Convert.ToInt64(validate.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 1)
                throw new UnauthorizedAccessException("An active global administrator is required; self-bootstrap is limited to the default company.");
        }
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = @"INSERT INTO monarch_user_company_access(user_id,company_id,role_code,is_active,granted_by_user_id,granted_utc)
VALUES($user,$company,$role,1,$actor,$now)
ON CONFLICT(user_id,company_id) DO UPDATE SET role_code=excluded.role_code,is_active=1,
granted_by_user_id=excluded.granted_by_user_id,granted_utc=excluded.granted_utc;";
            command.Parameters.AddWithValue("$user", userId.ToString());
            command.Parameters.AddWithValue("$company", companyId.ToString());
            command.Parameters.AddWithValue("$role", roleCode);
            command.Parameters.AddWithValue("$actor", grantedByUserId.ToString());
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }
        using (var audit = connection.CreateCommand())
        {
            audit.Transaction = transaction;
            audit.CommandText = @"INSERT INTO monarch_company_audit_log
(company_id,actor_user_id,action,entity_type,entity_id,details,occurred_utc)
VALUES($company,$actor,'CompanyAccessGranted','User',$user,$details,$now);";
            audit.Parameters.AddWithValue("$company", companyId.ToString());
            audit.Parameters.AddWithValue("$actor", grantedByUserId.ToString());
            audit.Parameters.AddWithValue("$user", userId.ToString());
            audit.Parameters.AddWithValue("$details", "role=" + roleCode);
            audit.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            audit.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    /// <summary>Revokes a user's entire company membership and branch grants atomically, preserving an audit trail.</summary>
    public void RevokeCompanyAccess(Guid userId, Guid companyId, Guid revokedByUserId)
    {
        if (userId == Guid.Empty || companyId == Guid.Empty || revokedByUserId == Guid.Empty)
            throw new ArgumentException("User, company and revoking administrator are required.");
        if (userId == revokedByUserId)
            throw new InvalidOperationException("لا يمكن سحب عضوية المنشأة من حساب المدير المستخدم حالياً.");

        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        string previousRole;
        long activeBranches;
        using (var validate = connection.CreateCommand())
        {
            validate.Transaction = transaction;
            validate.CommandText = @"SELECT a.role_code,
 (SELECT COUNT(*) FROM monarch_user_branch_access ba WHERE ba.user_id=a.user_id AND ba.company_id=a.company_id AND ba.is_active=1)
FROM monarch_user_company_access a
JOIN monarch_users actor ON actor.id=$actor AND actor.is_active=1
JOIN monarch_user_roles ur ON ur.user_id=actor.id
JOIN monarch_roles r ON r.id=ur.role_id AND r.code='Administrator' COLLATE NOCASE
WHERE a.user_id=$user AND a.company_id=$company AND a.is_active=1;";
            validate.Parameters.AddWithValue("$actor", revokedByUserId.ToString());
            validate.Parameters.AddWithValue("$user", userId.ToString());
            validate.Parameters.AddWithValue("$company", companyId.ToString());
            using var reader = validate.ExecuteReader();
            if (!reader.Read()) throw new UnauthorizedAccessException("An active global administrator and active company membership are required.");
            previousRole = reader.GetString(0);
            activeBranches = reader.GetInt64(1);
        }
        if (string.Equals(previousRole, "Administrator", StringComparison.OrdinalIgnoreCase))
        {
            using var remaining = connection.CreateCommand();
            remaining.Transaction = transaction;
            remaining.CommandText = @"SELECT COUNT(*) FROM monarch_user_company_access a
JOIN monarch_users u ON u.id=a.user_id AND u.is_active=1
WHERE a.company_id=$company AND a.is_active=1 AND a.role_code='Administrator' COLLATE NOCASE AND a.user_id<>$user;";
            remaining.Parameters.AddWithValue("$company", companyId.ToString());
            remaining.Parameters.AddWithValue("$user", userId.ToString());
            if (Convert.ToInt64(remaining.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 0)
                throw new InvalidOperationException("لا يمكن سحب عضوية آخر مدير نشط في المنشأة.");
        }
        using (var branches = connection.CreateCommand())
        {
            branches.Transaction = transaction;
            branches.CommandText = "UPDATE monarch_user_branch_access SET is_active=0 WHERE user_id=$user AND company_id=$company AND is_active=1;";
            branches.Parameters.AddWithValue("$user", userId.ToString());
            branches.Parameters.AddWithValue("$company", companyId.ToString());
            branches.ExecuteNonQuery();
        }
        using (var membership = connection.CreateCommand())
        {
            membership.Transaction = transaction;
            membership.CommandText = "UPDATE monarch_user_company_access SET is_active=0 WHERE user_id=$user AND company_id=$company AND is_active=1;";
            membership.Parameters.AddWithValue("$user", userId.ToString());
            membership.Parameters.AddWithValue("$company", companyId.ToString());
            if (membership.ExecuteNonQuery() != 1) throw new InvalidOperationException("Company membership changed concurrently.");
        }
        var details = "PreviousRole=" + previousRole + ";RevokedBranchGrants=" + activeBranches;
        using (var audit = connection.CreateCommand())
        {
            audit.Transaction = transaction;
            audit.CommandText = @"INSERT INTO monarch_company_audit_log
(company_id,actor_user_id,action,entity_type,entity_id,details,occurred_utc)
VALUES($company,$actor,'CompanyAccessRevoked','User',$user,$details,$now);";
            audit.Parameters.AddWithValue("$company", companyId.ToString());
            audit.Parameters.AddWithValue("$actor", revokedByUserId.ToString());
            audit.Parameters.AddWithValue("$user", userId.ToString());
            audit.Parameters.AddWithValue("$details", details);
            audit.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            audit.ExecuteNonQuery();
        }
        using (var audit = connection.CreateCommand())
        {
            audit.Transaction = transaction;
            audit.CommandText = @"INSERT INTO monarch_audit_log(user_id,action,entity_type,entity_id,details,occurred_utc)
VALUES($actor,'CompanyAccessRevoked','User',$user,$details,$now);";
            audit.Parameters.AddWithValue("$actor", revokedByUserId.ToString());
            audit.Parameters.AddWithValue("$user", userId.ToString());
            audit.Parameters.AddWithValue("$details", "Company=" + companyId.ToString() + ";" + details);
            audit.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            audit.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    /// <summary>Grants branch membership only to a company member and only by a global administrator.</summary>
    public void GrantBranchAccess(Guid userId, Guid companyId, Guid branchId, bool isActive, Guid grantedByUserId)
    {
        if (userId == Guid.Empty || companyId == Guid.Empty || branchId == Guid.Empty || grantedByUserId == Guid.Empty)
            throw new ArgumentException("User, company, branch and granting administrator are required.");
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using (var validate = connection.CreateCommand())
        {
            validate.Transaction = transaction;
            validate.CommandText = @"SELECT COUNT(*) FROM monarch_users actor
JOIN monarch_user_roles ur ON ur.user_id=actor.id
JOIN monarch_roles r ON r.id=ur.role_id AND r.code='Administrator' COLLATE NOCASE
JOIN monarch_user_company_access a ON a.user_id=$user AND a.company_id=$company AND a.is_active=1
JOIN monarch_branches b ON b.id=$branch AND b.company_id=$company AND b.is_active=1
JOIN monarch_users target ON target.id=$user AND target.is_active=1
WHERE actor.id=$actor AND actor.is_active=1;";
            validate.Parameters.AddWithValue("$user", userId.ToString());
            validate.Parameters.AddWithValue("$company", companyId.ToString());
            validate.Parameters.AddWithValue("$branch", branchId.ToString());
            validate.Parameters.AddWithValue("$actor", grantedByUserId.ToString());
            if (Convert.ToInt64(validate.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 1)
                throw new UnauthorizedAccessException("Global administrator, active membership and matching branch are required.");
        }
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = @"INSERT INTO monarch_user_branch_access(user_id,company_id,branch_id,is_active)
VALUES($user,$company,$branch,$active)
ON CONFLICT(user_id,branch_id) DO UPDATE SET company_id=excluded.company_id,is_active=excluded.is_active;";
            command.Parameters.AddWithValue("$user", userId.ToString());
            command.Parameters.AddWithValue("$company", companyId.ToString());
            command.Parameters.AddWithValue("$branch", branchId.ToString());
            command.Parameters.AddWithValue("$active", isActive ? 1 : 0);
            command.ExecuteNonQuery();
        }
        using (var audit = connection.CreateCommand())
        {
            audit.Transaction = transaction;
            audit.CommandText = @"INSERT INTO monarch_company_audit_log
(company_id,branch_id,actor_user_id,action,entity_type,entity_id,details,occurred_utc)
VALUES($company,$branch,$actor,'BranchAccessChanged','User',$user,$details,$now);";
            audit.Parameters.AddWithValue("$company", companyId.ToString());
            audit.Parameters.AddWithValue("$branch", branchId.ToString());
            audit.Parameters.AddWithValue("$actor", grantedByUserId.ToString());
            audit.Parameters.AddWithValue("$user", userId.ToString());
            audit.Parameters.AddWithValue("$details", "active=" + (isActive ? "true" : "false"));
            audit.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            audit.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public void GrantCompanyAndBranchAccess(Guid userId, Guid companyId, Guid branchId, string roleCode, Guid grantedByUserId)
    {
        if (userId == Guid.Empty || companyId == Guid.Empty || branchId == Guid.Empty || grantedByUserId == Guid.Empty)
            throw new ArgumentException("User, company, branch and granting actor are required.");
        if (!new[] { "Administrator", "Accountant", "Viewer" }.Contains(roleCode, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("Unknown company role.", nameof(roleCode));

        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using (var validate = connection.CreateCommand())
        {
            validate.Transaction = transaction;
            validate.CommandText = @"SELECT COUNT(*) FROM monarch_users u
JOIN monarch_companies c ON c.id=$company AND c.is_active=1
JOIN monarch_branches b ON b.id=$branch AND b.company_id=c.id AND b.is_active=1
JOIN monarch_users actor ON actor.id=$actor AND actor.is_active=1
JOIN monarch_user_roles actor_role ON actor_role.user_id=actor.id
JOIN monarch_roles r ON r.id=actor_role.role_id AND r.code='Administrator' COLLATE NOCASE
WHERE u.id=$user AND u.is_active=1;";
            validate.Parameters.AddWithValue("$user", userId.ToString());
            validate.Parameters.AddWithValue("$company", companyId.ToString());
            validate.Parameters.AddWithValue("$branch", branchId.ToString());
            validate.Parameters.AddWithValue("$actor", grantedByUserId.ToString());
            if (Convert.ToInt64(validate.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 1)
                throw new InvalidOperationException("Active user, company, branch and granting administrator are required.");
        }
        using (var companyAccess = connection.CreateCommand())
        {
            companyAccess.Transaction = transaction;
            companyAccess.CommandText = @"INSERT INTO monarch_user_company_access
(user_id,company_id,role_code,is_active,granted_by_user_id,granted_utc)
VALUES($user,$company,$role,1,$actor,$now)
ON CONFLICT(user_id,company_id) DO UPDATE SET role_code=excluded.role_code,is_active=1,
granted_by_user_id=excluded.granted_by_user_id,granted_utc=excluded.granted_utc;";
            companyAccess.Parameters.AddWithValue("$user", userId.ToString());
            companyAccess.Parameters.AddWithValue("$company", companyId.ToString());
            companyAccess.Parameters.AddWithValue("$role", roleCode);
            companyAccess.Parameters.AddWithValue("$actor", grantedByUserId.ToString());
            companyAccess.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            companyAccess.ExecuteNonQuery();
        }
        using (var branchAccess = connection.CreateCommand())
        {
            branchAccess.Transaction = transaction;
            branchAccess.CommandText = @"INSERT INTO monarch_user_branch_access(user_id,company_id,branch_id,is_active)
VALUES($user,$company,$branch,1)
ON CONFLICT(user_id,branch_id) DO UPDATE SET company_id=excluded.company_id,is_active=1;";
            branchAccess.Parameters.AddWithValue("$user", userId.ToString());
            branchAccess.Parameters.AddWithValue("$company", companyId.ToString());
            branchAccess.Parameters.AddWithValue("$branch", branchId.ToString());
            branchAccess.ExecuteNonQuery();
        }
        using (var audit = connection.CreateCommand())
        {
            audit.Transaction = transaction;
            audit.CommandText = @"INSERT INTO monarch_company_audit_log
(company_id,branch_id,actor_user_id,action,entity_type,entity_id,details,occurred_utc)
VALUES($company,$branch,$actor,'AccessGranted','User',$user,$details,$now);";
            audit.Parameters.AddWithValue("$company", companyId.ToString());
            audit.Parameters.AddWithValue("$branch", branchId.ToString());
            audit.Parameters.AddWithValue("$actor", grantedByUserId.ToString());
            audit.Parameters.AddWithValue("$user", userId.ToString());
            audit.Parameters.AddWithValue("$details", "role=" + roleCode);
            audit.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            audit.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public string GetCompanyBaseCurrency(Guid companyId)
    {
        using var connection=Open(); using var command=connection.CreateCommand();
        command.CommandText="SELECT base_currency_code FROM monarch_companies WHERE id=$company AND is_active=1;";
        command.Parameters.AddWithValue("$company",companyId.ToString());
        return (string?)command.ExecuteScalar() ?? throw new KeyNotFoundException("Active company currency was not found.");
    }

    public int GetCurrencyExponent(string currencyCode)
    {
        using var connection=Open(); using var command=connection.CreateCommand();
        command.CommandText="SELECT minor_unit_exponent FROM monarch_currencies WHERE code=$code AND is_active=1;";
        command.Parameters.AddWithValue("$code",currencyCode.ToUpperInvariant());
        var value=command.ExecuteScalar();
        return value == null ? throw new KeyNotFoundException("Active currency configuration was not found.") : Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    public bool HasLegacyLedgerData()
    {
        using var connection=Open(); using var command=connection.CreateCommand();
        command.CommandText="SELECT (SELECT COUNT(*) FROM monarch_accounts)+(SELECT COUNT(*) FROM monarch_journals);";
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture)>0;
    }

    public bool HasCompletedLegacyMigration(Guid companyId)
    {
        using var connection=Open(); using var command=connection.CreateCommand();
        command.CommandText="SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='monarch_migration_history';";
        if(Convert.ToInt64(command.ExecuteScalar(),System.Globalization.CultureInfo.InvariantCulture)==0) return false;
        command.CommandText="SELECT COUNT(*) FROM monarch_migration_history WHERE migration_id='legacy-ledger-to-tenant-v1' AND company_id=$company;";
        command.Parameters.AddWithValue("$company",companyId.ToString());
        return Convert.ToInt64(command.ExecuteScalar(),System.Globalization.CultureInfo.InvariantCulture)>0;
    }

    public bool HasCompletedLegacyMigration()
    {
        using var connection=Open(); using var command=connection.CreateCommand();
        command.CommandText="SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='monarch_migration_history';";
        if(Convert.ToInt64(command.ExecuteScalar(),System.Globalization.CultureInfo.InvariantCulture)==0) return false;
        command.CommandText="SELECT COUNT(*) FROM monarch_migration_history WHERE migration_id='legacy-ledger-to-tenant-v1';";
        return Convert.ToInt64(command.ExecuteScalar(),System.Globalization.CultureInfo.InvariantCulture)>0;
    }

    public bool HasCompanyMembership(Guid userId)
    {
        if (userId == Guid.Empty) return false;
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM monarch_user_company_access WHERE user_id=$user AND is_active=1;";
        command.Parameters.AddWithValue("$user", userId.ToString());
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) > 0;
    }

    // Retained for diagnostics and compatibility; startup authorization must use HasCompanyMembership(userId).
    public bool HasAnyCompanyMembership()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM monarch_user_company_access WHERE is_active=1;";
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) > 0;
    }

    public IReadOnlyList<CompanySummary> GetCompanies()
    {
        using var connection=Open(); using var command=connection.CreateCommand();
        command.CommandText="SELECT id,code,legal_name,display_name,country_code,base_currency_code,time_zone_id,is_active FROM monarch_companies ORDER BY code;";
        var rows=new List<CompanySummary>(); using var reader=command.ExecuteReader();
        while(reader.Read()) rows.Add(new CompanySummary(Guid.Parse(reader.GetString(0)),reader.GetString(1),reader.GetString(2),reader.GetString(3),reader.GetString(4),reader.GetString(5),reader.GetString(6),reader.GetInt64(7)==1));
        return rows.AsReadOnly();
    }

    public IReadOnlyList<BranchSummary> GetBranches(Guid companyId)
    {
        using var connection=Open(); using var command=connection.CreateCommand();
        command.CommandText="SELECT id,company_id,code,name,address,is_head_office,is_active FROM monarch_branches WHERE company_id=$company ORDER BY code;";
        command.Parameters.AddWithValue("$company",companyId.ToString()); var rows=new List<BranchSummary>(); using var reader=command.ExecuteReader();
        while(reader.Read()) rows.Add(new BranchSummary(Guid.Parse(reader.GetString(0)),Guid.Parse(reader.GetString(1)),reader.GetString(2),reader.GetString(3),reader.IsDBNull(4)?null:reader.GetString(4),reader.GetInt64(5)==1,reader.GetInt64(6)==1));
        return rows.AsReadOnly();
    }

    public IReadOnlyList<CompanyAccessSummary> GetUserCompanies(Guid userId)
    {
        using var connection=Open(); using var command=connection.CreateCommand();
        command.CommandText=@"SELECT c.id,c.display_name,a.role_code,a.is_active FROM monarch_user_company_access a
JOIN monarch_companies c ON c.id=a.company_id WHERE a.user_id=$user ORDER BY c.code;";
        command.Parameters.AddWithValue("$user",userId.ToString()); var rows=new List<CompanyAccessSummary>(); using var reader=command.ExecuteReader();
        while(reader.Read()) rows.Add(new CompanyAccessSummary(Guid.Parse(reader.GetString(0)),reader.GetString(1),reader.GetString(2),reader.GetInt64(3)==1));
        return rows.AsReadOnly();
    }

    /// <summary>Returns only active branches explicitly granted to the user, except company Administrators who can access all active branches.</summary>
    public IReadOnlyList<BranchSummary> GetAccessibleBranches(Guid userId, Guid companyId)
    {
        if (userId == Guid.Empty || companyId == Guid.Empty)
            throw new ArgumentException("Authenticated user and company are required.");
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = @"SELECT b.id,b.company_id,b.code,b.name,b.address,b.is_head_office,b.is_active
FROM monarch_branches b
JOIN monarch_user_company_access a ON a.company_id=b.company_id AND a.user_id=$user AND a.is_active=1
JOIN monarch_users u ON u.id=a.user_id AND u.is_active=1
JOIN monarch_companies c ON c.id=b.company_id AND c.is_active=1
WHERE b.company_id=$company AND b.is_active=1
AND (a.role_code='Administrator' OR EXISTS (
 SELECT 1 FROM monarch_user_branch_access ba
 WHERE ba.user_id=$user AND ba.company_id=b.company_id AND ba.branch_id=b.id AND ba.is_active=1))
ORDER BY b.is_head_office DESC,b.code;";
        command.Parameters.AddWithValue("$user", userId.ToString());
        command.Parameters.AddWithValue("$company", companyId.ToString());
        var rows = new List<BranchSummary>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) rows.Add(new BranchSummary(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)),
            reader.GetString(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetInt64(5) == 1, reader.GetInt64(6) == 1));
        return rows.AsReadOnly();
    }

    private static void Validate(string code,string legalName,string displayName,string country,string currency,string zone)
    {
        if(string.IsNullOrWhiteSpace(code)||code.Trim().Length>32) throw new ArgumentException("Company code must be 1-32 characters.",nameof(code));
        if(string.IsNullOrWhiteSpace(legalName)||legalName.Trim().Length>200) throw new ArgumentException("Legal name must be 1-200 characters.",nameof(legalName));
        if(string.IsNullOrWhiteSpace(displayName)||displayName.Trim().Length>120) throw new ArgumentException("Display name must be 1-120 characters.",nameof(displayName));
        if(string.IsNullOrWhiteSpace(country)||country.Trim().Length!=2) throw new ArgumentException("Country must be an ISO alpha-2 code.",nameof(country));
        if(string.IsNullOrWhiteSpace(currency)||currency.Trim().Length!=3) throw new ArgumentException("Currency must be an ISO 4217 code.",nameof(currency));
        if(string.IsNullOrWhiteSpace(zone)||zone.Trim().Length>80) throw new ArgumentException("A valid time-zone identifier is required.",nameof(zone));
    }
    private SqliteConnection Open(){var c=new SqliteConnection(_connectionString);c.Open();using var cmd=c.CreateCommand();cmd.CommandText="PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";cmd.ExecuteNonQuery();return c;}
}
