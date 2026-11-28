using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;

namespace Monarch.Core.Security;

/// <summary>Offline identity store with salted PBKDF2 password hashes, lockout and role permissions.</summary>
public sealed class IdentityStore
{
    private const int Iterations = 210000;
    private const int SaltSize = 16;
    private const int HashSize = 32;
    private const int MaxFailedAttempts = 5;
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);
    private readonly string _connectionString;

    public IdentityStore(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath)) throw new ArgumentException("Database path is required.", nameof(databasePath));
        var fullPath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = fullPath, ForeignKeys = true }.ToString();
    }

    public void Initialize(string? schemaPath = null)
    {
        schemaPath ??= Path.Combine(AppContext.BaseDirectory, "database", "security.sql");
        if (!File.Exists(schemaPath)) throw new FileNotFoundException("MONARCH security schema was not found.", schemaPath);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = File.ReadAllText(schemaPath);
        command.ExecuteNonQuery();
    }

    public bool HasUsers()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM monarch_users);";
        return Convert.ToInt64(command.ExecuteScalar()) == 1;
    }

    public void CreateFirstAdministrator(string username, string displayName, string password)
    {
        ValidateIdentity(username, displayName, password);
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using (var count = connection.CreateCommand())
        {
            count.Transaction = transaction;
            count.CommandText = "SELECT COUNT(*) FROM monarch_users;";
            if (Convert.ToInt64(count.ExecuteScalar()) != 0) throw new InvalidOperationException("Initial administrator already exists.");
        }
        InsertUser(connection, transaction, username, displayName, password, "Administrator", null);
        transaction.Commit();
    }

    public UserSession? Authenticate(string username, string password)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        string? id = null, displayName = null, role = null, lockout = null;
        byte[]? salt = null, expected = null;
        int iterations = 0, failed = 0;
        bool active = false;
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT u.id,u.display_name,u.password_salt,u.password_hash,u.password_iterations,u.is_active,u.failed_login_count,u.lockout_until_utc,(SELECT r.code FROM monarch_user_roles ur JOIN monarch_roles r ON r.id=ur.role_id WHERE ur.user_id=u.id ORDER BY r.code LIMIT 1) FROM monarch_users u WHERE u.username=$username COLLATE NOCASE;";
            command.Parameters.AddWithValue("$username", (username ?? string.Empty).Trim());
            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                id = reader.GetString(0); displayName = reader.GetString(1); salt = (byte[])reader[2]; expected = (byte[])reader[3];
                iterations = reader.GetInt32(4); active = reader.GetInt64(5) == 1; failed = reader.GetInt32(6);
                lockout = reader.IsDBNull(7) ? null : reader.GetString(7); role = reader.IsDBNull(8) ? null : reader.GetString(8);
            }
        }

        if (id == null || salt == null || expected == null || !active || role == null)
        {
            WriteAudit(connection, transaction, null, "LoginFailed", "User", null, "Unknown or inactive account");
            transaction.Commit();
            return null;
        }
        if (lockout != null && DateTimeOffset.Parse(lockout).UtcDateTime > DateTime.UtcNow)
        {
            WriteAudit(connection, transaction, Guid.Parse(id), "LoginBlocked", "User", id, "Account is temporarily locked");
            transaction.Commit();
            return null;
        }

        var actual = Derive(password ?? string.Empty, salt, iterations);
        if (!FixedTimeEquals(actual, expected))
        {
            failed++;
            var until = failed >= MaxFailedAttempts ? DateTimeOffset.UtcNow.Add(LockoutDuration).ToString("O") : null;
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE monarch_users SET failed_login_count=$failed,lockout_until_utc=$until WHERE id=$id;";
            update.Parameters.AddWithValue("$failed", failed);
            update.Parameters.AddWithValue("$until", (object?)until ?? DBNull.Value);
            update.Parameters.AddWithValue("$id", id);
            update.ExecuteNonQuery();
            WriteAudit(connection, transaction, Guid.Parse(id), "LoginFailed", "User", id, "Invalid password");
            transaction.Commit();
            return null;
        }

        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = "UPDATE monarch_users SET failed_login_count=0,lockout_until_utc=NULL,last_login_utc=$now WHERE id=$id;";
            update.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            update.Parameters.AddWithValue("$id", id);
            update.ExecuteNonQuery();
        }
        var permissions = new List<string>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT DISTINCT rp.permission_code FROM monarch_user_roles ur JOIN monarch_role_permissions rp ON rp.role_id=ur.role_id WHERE ur.user_id=$id ORDER BY rp.permission_code;";
            command.Parameters.AddWithValue("$id", id);
            using var reader = command.ExecuteReader();
            while (reader.Read()) permissions.Add(reader.GetString(0));
        }
        WriteAudit(connection, transaction, Guid.Parse(id), "LoginSucceeded", "User", id, null);
        transaction.Commit();
        return new UserSession(Guid.Parse(id), username.Trim(), displayName!, role!, permissions);
    }

    public void CreateUser(UserSession actor, string username, string displayName, string password, string roleCode)
    {
        Require(actor, "users.manage");
        ValidateIdentity(username, displayName, password);
        if (!new[] { "Administrator", "Accountant", "Viewer" }.Contains(roleCode, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("Unknown role.");
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        InsertUser(connection, transaction, username, displayName, password, roleCode, actor.UserId);
        transaction.Commit();
    }

    /// <summary>Creates a user and records tenant-scoped audit in the same SQLite transaction.</summary>
    public Guid CreateUser(UserSession actor, string username, string displayName, string password, string roleCode,
        Guid companyId, Guid branchId)
    {
        Require(actor, "users.manage");
        ValidateIdentity(username, displayName, password);
        ValidateTenantScope(companyId, branchId);
        if (!new[] { "Administrator", "Accountant", "Viewer" }.Contains(roleCode, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("Unknown role.");
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        RequireCompanyBranchAccess(connection, transaction, actor.UserId, companyId, branchId);
        var id = InsertUser(connection, transaction, username, displayName, password, roleCode, actor.UserId);
        WriteCompanyAudit(connection, transaction, actor.UserId, companyId, branchId,
            "UserCreated", id, "Role=" + roleCode);
        transaction.Commit();
        return id;
    }

    /// <summary>Changes account activation and writes tenant audit atomically.</summary>
    public void SetUserActive(UserSession actor, Guid userId, bool active, Guid companyId, Guid branchId)
    {
        Require(actor, "users.manage");
        ValidateTenantScope(companyId, branchId);
        if (userId == Guid.Empty) throw new ArgumentException("User id is required.", nameof(userId));
        if (actor.UserId == userId && !active) throw new InvalidOperationException("You cannot deactivate your own active session account.");
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        RequireCompanyBranchAccess(connection, transaction, actor.UserId, companyId, branchId);
        if (!active)
        {
            using var admins = connection.CreateCommand();
            admins.Transaction = transaction;
            admins.CommandText = "SELECT COUNT(*) FROM monarch_users u JOIN monarch_user_roles ur ON ur.user_id=u.id JOIN monarch_roles r ON r.id=ur.role_id WHERE u.is_active=1 AND r.code='Administrator' COLLATE NOCASE;";
            if (Convert.ToInt64(admins.ExecuteScalar()) <= 1)
                throw new InvalidOperationException("لا يمكن تعطيل آخر حساب مدير نشط.");
        }
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE monarch_users SET is_active=$active WHERE id=$id;";
        command.Parameters.AddWithValue("$active", active ? 1 : 0);
        command.Parameters.AddWithValue("$id", userId.ToString("D"));
        if (command.ExecuteNonQuery() != 1) throw new KeyNotFoundException("User not found.");
        WriteAudit(connection, transaction, actor.UserId, active ? "UserActivated" : "UserDeactivated", "User", userId.ToString("D"), null);
        WriteCompanyAudit(connection, transaction, actor.UserId, companyId, branchId,
            active ? "UserActivated" : "UserDeactivated", userId, null);
        transaction.Commit();
    }

    /// <summary>Reassigns the global identity role and writes both audit records atomically.</summary>
    public void SetUserRole(UserSession actor, Guid userId, string roleCode, Guid companyId, Guid branchId)
    {
        Require(actor, "users.manage");
        ValidateTenantScope(companyId, branchId);
        if (userId == Guid.Empty) throw new ArgumentException("User id is required.", nameof(userId));
        if (actor.UserId == userId) throw new InvalidOperationException("لا يمكن تغيير دور الحساب المستخدم حالياً من هذه الجلسة.");
        if (!new[] { "Administrator", "Accountant", "Viewer" }.Contains(roleCode, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("Unknown role.");
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        RequireCompanyBranchAccess(connection, transaction, actor.UserId, companyId, branchId);
        using (var target = connection.CreateCommand())
        {
            target.Transaction = transaction;
            target.CommandText = @"SELECT EXISTS(SELECT 1 FROM monarch_user_company_access ca
JOIN monarch_user_branch_access ba ON ba.user_id=ca.user_id AND ba.company_id=ca.company_id
WHERE ca.user_id=$user AND ca.company_id=$company AND ba.branch_id=$branch
AND ca.is_active=1 AND ba.is_active=1);";
            target.Parameters.AddWithValue("$user", userId.ToString("D"));
            target.Parameters.AddWithValue("$company", companyId.ToString("D"));
            target.Parameters.AddWithValue("$branch", branchId.ToString("D"));
            if (Convert.ToInt64(target.ExecuteScalar()) != 1)
                throw new UnauthorizedAccessException("المستخدم المستهدف لا يملك عضوية نشطة في المنشأة والفرع المحددين.");
        }
        string previousRole;
        using (var current = connection.CreateCommand())
        {
            current.Transaction = transaction;
            current.CommandText = @"SELECT r.code FROM monarch_user_roles ur JOIN monarch_roles r ON r.id=ur.role_id
WHERE ur.user_id=$user ORDER BY r.code LIMIT 1;";
            current.Parameters.AddWithValue("$user", userId.ToString("D"));
            previousRole = current.ExecuteScalar() as string ?? throw new KeyNotFoundException("User role not found.");
        }
        if (string.Equals(previousRole, roleCode, StringComparison.OrdinalIgnoreCase))
        { transaction.Commit(); return; }
        using (var remove = connection.CreateCommand())
        {
            remove.Transaction = transaction;
            remove.CommandText = "DELETE FROM monarch_user_roles WHERE user_id=$user;";
            remove.Parameters.AddWithValue("$user", userId.ToString("D"));
            remove.ExecuteNonQuery();
        }
        using (var add = connection.CreateCommand())
        {
            add.Transaction = transaction;
            add.CommandText = @"INSERT INTO monarch_user_roles(user_id,role_id)
SELECT $user,id FROM monarch_roles WHERE code=$role COLLATE NOCASE;";
            add.Parameters.AddWithValue("$user", userId.ToString("D"));
            add.Parameters.AddWithValue("$role", roleCode);
            if (add.ExecuteNonQuery() != 1) throw new InvalidOperationException("Role configuration is missing.");
        }
        var details = "PreviousRole=" + previousRole + ";NewRole=" + roleCode;
        WriteAudit(connection, transaction, actor.UserId, "UserRoleChanged", "User", userId.ToString("D"), details);
        WriteCompanyAudit(connection, transaction, actor.UserId, companyId, branchId, "UserRoleChanged", userId, details);
        transaction.Commit();
    }

    public IReadOnlyList<UserSummary> GetUsers(UserSession actor)
    {
        Require(actor, "users.manage");
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT u.id,u.username,u.display_name,COALESCE(r.code,'Viewer'),u.is_active,u.created_utc FROM monarch_users u LEFT JOIN monarch_user_roles ur ON ur.user_id=u.id LEFT JOIN monarch_roles r ON r.id=ur.role_id ORDER BY u.username;";
        var users = new List<UserSummary>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) users.Add(new UserSummary(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt64(4) == 1, DateTime.Parse(reader.GetString(5)).ToUniversalTime()));
        return users.AsReadOnly();
    }

    public void SetUserActive(UserSession actor, Guid userId, bool active)
    {
        Require(actor, "users.manage");
        if (actor.UserId == userId && !active) throw new InvalidOperationException("You cannot deactivate your own active session account.");
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        if (!active)
        {
            using var admins = connection.CreateCommand();
            admins.Transaction = transaction;
            admins.CommandText = "SELECT COUNT(*) FROM monarch_users u JOIN monarch_user_roles ur ON ur.user_id=u.id JOIN monarch_roles r ON r.id=ur.role_id WHERE u.is_active=1 AND r.code='Administrator' COLLATE NOCASE;";
            if (Convert.ToInt64(admins.ExecuteScalar()) <= 1)
                throw new InvalidOperationException("لا يمكن تعطيل آخر حساب مدير نشط.");
        }
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE monarch_users SET is_active=$active WHERE id=$id;";
        command.Parameters.AddWithValue("$active", active ? 1 : 0);
        command.Parameters.AddWithValue("$id", userId.ToString());
        if (command.ExecuteNonQuery() != 1) throw new KeyNotFoundException("User not found.");
        WriteAudit(connection, transaction, actor.UserId, active ? "UserActivated" : "UserDeactivated", "User", userId.ToString(), null);
        transaction.Commit();
    }

    public static void Require(UserSession session, string permission)
    {
        if (session == null || !session.HasPermission(permission)) throw new UnauthorizedAccessException("ليس لديك صلاحية لتنفيذ هذه العملية.");
    }

    private static Guid InsertUser(SqliteConnection connection, SqliteTransaction transaction, string username, string displayName, string password, string roleCode, Guid? actorId)
    {
        var salt = new byte[SaltSize];
        using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(salt);
        var hash = Derive(password, salt, Iterations);
        var id = Guid.NewGuid();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO monarch_users(id,username,display_name,password_salt,password_hash,password_iterations,is_active,created_utc) VALUES($id,$username,$display,$salt,$hash,$iterations,1,$created);";
            command.Parameters.AddWithValue("$id", id.ToString()); command.Parameters.AddWithValue("$username", username.Trim());
            command.Parameters.AddWithValue("$display", displayName.Trim()); command.Parameters.AddWithValue("$salt", salt);
            command.Parameters.AddWithValue("$hash", hash); command.Parameters.AddWithValue("$iterations", Iterations);
            command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO monarch_user_roles(user_id,role_id) SELECT $user,id FROM monarch_roles WHERE code=$role COLLATE NOCASE;";
            command.Parameters.AddWithValue("$user", id.ToString()); command.Parameters.AddWithValue("$role", roleCode);
            if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("Role configuration is missing.");
        }
        WriteAudit(connection, transaction, actorId, "UserCreated", "User", id.ToString(), "Role=" + roleCode);
        return id;
    }

    private static void ValidateTenantScope(Guid companyId, Guid branchId)
    {
        if (companyId == Guid.Empty || branchId == Guid.Empty)
            throw new ArgumentException("Company and branch scope are required.");
    }

    private static void RequireCompanyBranchAccess(SqliteConnection connection, SqliteTransaction transaction,
        Guid actor, Guid company, Guid branch)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"SELECT EXISTS(
SELECT 1 FROM monarch_user_company_access ca
JOIN monarch_user_branch_access ba ON ba.user_id=ca.user_id AND ba.company_id=ca.company_id
JOIN monarch_branches b ON b.id=ba.branch_id AND b.company_id=ba.company_id
WHERE ca.user_id=$user AND ca.company_id=$company AND ba.branch_id=$branch
AND ca.is_active=1 AND ba.is_active=1 AND b.is_active=1);";
        command.Parameters.AddWithValue("$user", actor.ToString("D"));
        command.Parameters.AddWithValue("$company", company.ToString("D"));
        command.Parameters.AddWithValue("$branch", branch.ToString("D"));
        if (Convert.ToInt64(command.ExecuteScalar()) != 1)
            throw new UnauthorizedAccessException("المستخدم لا يملك وصولاً نشطاً إلى المنشأة والفرع المحددين.");
    }

    private static void WriteCompanyAudit(SqliteConnection connection, SqliteTransaction transaction,
        Guid actor, Guid company, Guid branch, string action, Guid subject, string? details)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"INSERT INTO monarch_company_audit_log
(company_id,branch_id,actor_user_id,action,entity_type,entity_id,details,occurred_utc)
VALUES($company,$branch,$actor,$action,'User',$subject,$details,$now);";
        command.Parameters.AddWithValue("$company", company.ToString("D"));
        command.Parameters.AddWithValue("$branch", branch.ToString("D"));
        command.Parameters.AddWithValue("$actor", actor.ToString("D"));
        command.Parameters.AddWithValue("$action", action);
        command.Parameters.AddWithValue("$subject", subject.ToString("D"));
        command.Parameters.AddWithValue("$details", (object?)details ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    private static void ValidateIdentity(string username, string displayName, string password)
    {
        if (string.IsNullOrWhiteSpace(username) || username.Trim().Length < 3 || username.Trim().Length > 64)
            throw new ArgumentException("اسم المستخدم يجب أن يكون بين 3 و64 حرفاً.");
        if (string.IsNullOrWhiteSpace(displayName) || displayName.Trim().Length > 120)
            throw new ArgumentException("أدخل اسماً ظاهراً صحيحاً.");
        if (string.IsNullOrEmpty(password) || password.Length < 12 || password.Length > 256)
            throw new ArgumentException("كلمة المرور يجب ألا تقل عن 12 حرفاً.");
    }

    private static byte[] Derive(string password, byte[] salt, int iterations)
    {
        // PBKDF2-HMAC-SHA256 implemented locally to retain .NET Framework 4.6.1 compatibility.
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        var block = new byte[salt.Length + 4];
        Buffer.BlockCopy(salt, 0, block, 0, salt.Length);
        block[block.Length - 1] = 1; // big-endian block index 1
        using var hmac = new HMACSHA256(passwordBytes);
        var u = hmac.ComputeHash(block);
        var result = (byte[])u.Clone();
        for (var round = 1; round < iterations; round++)
        {
            u = hmac.ComputeHash(u);
            for (var i = 0; i < result.Length; i++) result[i] ^= u[i];
        }
        Array.Clear(passwordBytes, 0, passwordBytes.Length);
        return result;
    }

    private static bool FixedTimeEquals(byte[] left, byte[] right)
    {
        if (left == null || right == null || left.Length != right.Length) return false;
        var diff = 0;
        for (var i = 0; i < left.Length; i++) diff |= left[i] ^ right[i];
        return diff == 0;
    }

    private static void WriteAudit(SqliteConnection connection, SqliteTransaction transaction, Guid? userId, string action, string entity, string? entityId, string? details)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO monarch_audit_log(user_id,action,entity_type,entity_id,details,occurred_utc) VALUES($user,$action,$entity,$entityId,$details,$now);";
        command.Parameters.AddWithValue("$user", (object?)userId?.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("$action", action); command.Parameters.AddWithValue("$entity", entity);
        command.Parameters.AddWithValue("$entityId", (object?)entityId ?? DBNull.Value); command.Parameters.AddWithValue("$details", (object?)details ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        command.ExecuteNonQuery();
        return connection;
    }
}
