using Monarch.Core.Abstractions;
using Npgsql;
using NpgsqlTypes;

namespace Monarch.PostgreSql.Infrastructure;

/// <summary>PostgreSQL implementation of the storage-agnostic General Ledger contract.</summary>
public sealed class PostgreSqlGeneralLedgerStore(PostgreSqlConnectionFactory factory) : IGeneralLedgerStore
{
    public async Task CreateAccountAsync(TenantScope tenant, GeneralLedgerAccount account, CancellationToken cancellationToken = default)
    {
        ValidateTenant(tenant);
        if (account.AccountId == Guid.Empty || string.IsNullOrWhiteSpace(account.AccountCode) ||
            string.IsNullOrWhiteSpace(account.AccountName) ||
            !new[] { "ASSET", "LIABILITY", "EQUITY", "REVENUE", "EXPENSE" }.Contains(account.AccountType, StringComparer.Ordinal) ||
            account.NormalSide is not ("D" or "C"))
            throw new ArgumentException("Invalid general ledger account.");

        var normalizedCode = account.AccountCode.Trim();
        var normalizedName = account.AccountName.Trim();
        await using var scope = await BeginAsync(tenant, cancellationToken);
        await using var command = new NpgsqlCommand("""
            INSERT INTO monarch_gl_accounts(company_id, account_id, account_code, account_name, account_type, normal_side)
            VALUES (@company, @id, @code, @name, @type, @side)
            ON CONFLICT DO NOTHING
            RETURNING account_id
            """, scope.Connection, scope.Transaction);
        command.Parameters.AddWithValue("company", tenant.CompanyId);
        command.Parameters.AddWithValue("id", account.AccountId);
        command.Parameters.AddWithValue("code", normalizedCode);
        command.Parameters.AddWithValue("name", normalizedName);
        command.Parameters.AddWithValue("type", account.AccountType);
        command.Parameters.AddWithValue("side", account.NormalSide);
        var insertedId = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        if (insertedId is null)
        {
            await using var existing = new NpgsqlCommand("""
                SELECT account_id, account_code, account_name, account_type, normal_side
                FROM monarch_gl_accounts
                WHERE company_id=@company AND (account_id=@id OR account_code=@code)
                LIMIT 1
                """, scope.Connection, scope.Transaction);
            existing.Parameters.AddWithValue("company", tenant.CompanyId);
            existing.Parameters.AddWithValue("id", account.AccountId);
            existing.Parameters.AddWithValue("code", normalizedCode);
            await using (var reader = await existing.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
                    reader.GetGuid(0) != account.AccountId ||
                    !string.Equals(reader.GetString(1), normalizedCode, StringComparison.Ordinal) ||
                    !string.Equals(reader.GetString(2), normalizedName, StringComparison.Ordinal) ||
                    !string.Equals(reader.GetString(3), account.AccountType, StringComparison.Ordinal) ||
                    !string.Equals(reader.GetString(4), account.NormalSide, StringComparison.Ordinal))
                    throw new InvalidOperationException("An account ID or code already exists with different details.");
            }
        }

        // Account creation and its durable audit/integration event share one transaction.
        // Using the account ID as the event ID makes retries idempotent.
        await new PostgreSqlOutboxWriter().EnqueueAsync(scope, account.AccountId,
            "GeneralLedgerAccount", account.AccountId.ToString("D"), "GeneralLedger.AccountCreated",
            new
            {
                companyId = tenant.CompanyId,
                branchId = tenant.BranchId,
                actorUserId = scope.Tenant.UserId,
                accountId = account.AccountId,
                accountCode = normalizedCode,
                accountName = normalizedName,
                accountType = account.AccountType,
                normalSide = account.NormalSide
            }, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);

        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<GeneralLedgerAccount>> GetAccountsAsync(
        TenantScope tenant, bool includeInactive = false, CancellationToken cancellationToken = default)
    {
        ValidateTenant(tenant);
        await using var scope = await BeginAsync(tenant, cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT account_id, account_code, account_name, account_type, normal_side
            FROM monarch_gl_accounts
            WHERE company_id=@company AND (@include_inactive OR is_active)
            ORDER BY account_code
            """, scope.Connection, scope.Transaction);
        command.Parameters.AddWithValue("company", tenant.CompanyId);
        command.Parameters.AddWithValue("include_inactive", includeInactive);
        var accounts = new List<GeneralLedgerAccount>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                accounts.Add(new GeneralLedgerAccount(reader.GetGuid(0), reader.GetString(1),
                    reader.GetString(2), reader.GetString(3), reader.GetString(4)));
        }
        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        return accounts;
    }

    public async Task<GeneralLedgerJournal> CreateDraftAsync(TenantScope tenant, Guid journalId, int fiscalYear,
        DateOnly journalDate, string description, string currencyCode,
        IReadOnlyCollection<GeneralLedgerLineInput> lines, Guid? reversesJournalId = null,
        CancellationToken cancellationToken = default)
    {
        ValidateTenant(tenant);
        if (journalId == Guid.Empty || fiscalYear != journalDate.Year || fiscalYear is < 2000 or > 2200)
            throw new ArgumentException("Journal ID and matching supported fiscal year are required.");
        if (string.IsNullOrWhiteSpace(description) || string.IsNullOrWhiteSpace(currencyCode) ||
            currencyCode.Trim().Length != 3 || !currencyCode.Trim().All(char.IsAsciiLetter) || lines is null || lines.Count < 2)
            throw new ArgumentException("Journal description, ISO currency code and at least two lines are required.");
        if (lines.Any(x => x.AccountId == Guid.Empty || x.DebitMinor < 0 || x.CreditMinor < 0 ||
            (x.DebitMinor == 0 && x.CreditMinor == 0) || (x.DebitMinor > 0 && x.CreditMinor > 0)))
            throw new ArgumentException("Each journal line must contain one positive minor-unit debit or credit.");
        if (lines.Sum(x => (decimal)x.DebitMinor) != lines.Sum(x => (decimal)x.CreditMinor))
            throw new ArgumentException("A journal draft must be balanced before persistence.");

        await using var scope = await BeginAsync(tenant, cancellationToken);
        if (reversesJournalId.HasValue)
        {
            string? originalCurrency;
            await using (var original = new NpgsqlCommand("""
                SELECT currency_code FROM monarch_gl_journals j
                WHERE j.company_id=@company AND j.branch_id=@branch AND j.journal_id=@original
                  AND j.status='POSTED'
                  AND NOT EXISTS (SELECT 1 FROM monarch_gl_journals r
                    WHERE r.company_id=j.company_id AND r.branch_id=j.branch_id AND r.reverses_journal_id=j.journal_id)
                """, scope.Connection, scope.Transaction))
            {
                original.Parameters.AddWithValue("company", tenant.CompanyId);
                original.Parameters.AddWithValue("branch", tenant.BranchId);
                original.Parameters.AddWithValue("original", reversesJournalId.Value);
                originalCurrency = (string?)await original.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            }
            if (originalCurrency is null || !string.Equals(originalCurrency.Trim(), currencyCode.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("A reversal must reference an unreversed posted journal in the same currency.");

            var expected = new Dictionary<Guid, (long Debit, long Credit)>();
            await using (var originalLines = new NpgsqlCommand("""
                SELECT account_id, sum(credit_minor)::bigint, sum(debit_minor)::bigint
                FROM monarch_gl_journal_lines
                WHERE company_id=@company AND branch_id=@branch AND journal_id=@journal
                GROUP BY account_id
                """, scope.Connection, scope.Transaction))
            {
                originalLines.Parameters.AddWithValue("company", tenant.CompanyId);
                originalLines.Parameters.AddWithValue("branch", tenant.BranchId);
                originalLines.Parameters.AddWithValue("journal", reversesJournalId.Value);
                await using var reader = await originalLines.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    expected.Add(reader.GetGuid(0), (reader.GetInt64(1), reader.GetInt64(2)));
            }
            var supplied = lines.GroupBy(x => x.AccountId).ToDictionary(
                group => group.Key,
                group => (group.Sum(x => x.DebitMinor), group.Sum(x => x.CreditMinor)));
            if (expected.Count != supplied.Count || expected.Any(pair =>
                !supplied.TryGetValue(pair.Key, out var actual) ||
                actual.Item1 != pair.Value.Debit || actual.Item2 != pair.Value.Credit))
                throw new InvalidOperationException("Reversal lines must exactly invert the original journal by account and minor units.");
        }

        foreach (var accountId in lines.Select(x => x.AccountId).Distinct())
        {
            await using var accountCheck = new NpgsqlCommand("""
                SELECT EXISTS(SELECT 1 FROM monarch_gl_accounts
                  WHERE company_id=@company AND account_id=@account AND is_active)
                """, scope.Connection, scope.Transaction);
            accountCheck.Parameters.AddWithValue("company", tenant.CompanyId);
            accountCheck.Parameters.AddWithValue("account", accountId);
            if ((bool)(await accountCheck.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? false) is false)
                throw new InvalidOperationException($"Account {accountId} is missing or inactive.");
        }

        long journalNumber;
        await using (var allocate = new NpgsqlCommand(
            "SELECT monarch_gl_next_journal_number(@company, @branch, @year)", scope.Connection, scope.Transaction))
        {
            allocate.Parameters.AddWithValue("company", tenant.CompanyId);
            allocate.Parameters.AddWithValue("branch", tenant.BranchId);
            allocate.Parameters.AddWithValue("year", fiscalYear);
            journalNumber = Convert.ToInt64(await allocate.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        }

        await using (var header = new NpgsqlCommand("""
            INSERT INTO monarch_gl_journals(company_id, branch_id, journal_id, fiscal_year, journal_number,
              journal_date, description, currency_code, status, created_by, reverses_journal_id)
            VALUES(@company,@branch,@id,@year,@number,@date,@description,@currency,'DRAFT',@user,@reverses)
            """, scope.Connection, scope.Transaction))
        {
            header.Parameters.AddWithValue("company", tenant.CompanyId);
            header.Parameters.AddWithValue("branch", tenant.BranchId);
            header.Parameters.AddWithValue("id", journalId);
            header.Parameters.AddWithValue("year", fiscalYear);
            header.Parameters.AddWithValue("number", journalNumber);
            header.Parameters.AddWithValue("date", journalDate);
            header.Parameters.AddWithValue("description", description.Trim());
            header.Parameters.AddWithValue("currency", currencyCode.Trim().ToUpperInvariant());
            header.Parameters.AddWithValue("user", scope.Tenant.UserId);
            header.Parameters.Add("reverses", NpgsqlDbType.Uuid).Value = (object?)reversesJournalId ?? DBNull.Value;
            await header.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var lineNumber = 0;
        foreach (var line in lines)
        {
            await using var insert = new NpgsqlCommand("""
                INSERT INTO monarch_gl_journal_lines(company_id, branch_id, journal_id, line_number,
                  account_id, description, debit_minor, credit_minor)
                VALUES(@company,@branch,@journal,@line,@account,@description,@debit,@credit)
                """, scope.Connection, scope.Transaction);
            insert.Parameters.AddWithValue("company", tenant.CompanyId);
            insert.Parameters.AddWithValue("branch", tenant.BranchId);
            insert.Parameters.AddWithValue("journal", journalId);
            insert.Parameters.AddWithValue("line", ++lineNumber);
            insert.Parameters.AddWithValue("account", line.AccountId);
            insert.Parameters.AddWithValue("description", (object?)line.Description ?? DBNull.Value);
            insert.Parameters.AddWithValue("debit", line.DebitMinor);
            insert.Parameters.AddWithValue("credit", line.CreditMinor);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new GeneralLedgerJournal(journalId, journalNumber, fiscalYear, journalDate, currencyCode.Trim().ToUpperInvariant(), "DRAFT");
    }

    public async Task<IReadOnlyList<GeneralLedgerJournalSummary>> GetJournalsAsync(
        TenantScope tenant, int limit = 250, CancellationToken cancellationToken = default)
    {
        ValidateTenant(tenant);
        if (limit is < 1 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(limit), "Journal list limit must be between 1 and 1000.");
        await using var scope = await BeginAsync(tenant, cancellationToken);
        const string sql = """
            SELECT j.journal_id, j.journal_number, j.fiscal_year, j.journal_date,
                   j.description, j.currency_code, j.status,
                   COALESCE(sum(l.debit_minor), 0)::bigint AS total_debit_minor
            FROM monarch_gl_journals j
            LEFT JOIN monarch_gl_journal_lines l ON l.company_id=j.company_id
              AND l.branch_id=j.branch_id AND l.journal_id=j.journal_id
            WHERE j.company_id=@company AND j.branch_id=@branch
            GROUP BY j.journal_id, j.journal_number, j.fiscal_year, j.journal_date,
                     j.description, j.currency_code, j.status
            ORDER BY j.journal_date DESC, j.journal_number DESC
            LIMIT @limit
            """;
        await using var command = new NpgsqlCommand(sql, scope.Connection, scope.Transaction);
        command.Parameters.AddWithValue("company", tenant.CompanyId);
        command.Parameters.AddWithValue("branch", tenant.BranchId);
        command.Parameters.AddWithValue("limit", limit);
        var rows = new List<GeneralLedgerJournalSummary>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                rows.Add(new GeneralLedgerJournalSummary(reader.GetGuid(0), reader.GetInt64(1),
                    reader.GetInt32(2), reader.GetFieldValue<DateOnly>(3), reader.GetString(4),
                    reader.GetString(5).Trim(), reader.GetString(6), reader.GetInt64(7)));
        }
        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        return rows;
    }

    public async Task<GeneralLedgerJournalDetail?> GetJournalAsync(
        TenantScope tenant, Guid journalId, CancellationToken cancellationToken = default)
    {
        ValidateTenant(tenant);
        if (journalId == Guid.Empty) throw new ArgumentException("Journal ID is required.", nameof(journalId));
        await using var scope = await BeginAsync(tenant, cancellationToken);
        GeneralLedgerJournalDetail? detail = null;
        await using (var header = new NpgsqlCommand("""
            SELECT journal_number, fiscal_year, journal_date, currency_code, status,
                   description, created_by, posted_by, reverses_journal_id
            FROM monarch_gl_journals
            WHERE company_id=@company AND branch_id=@branch AND journal_id=@journal
            """, scope.Connection, scope.Transaction))
        {
            header.Parameters.AddWithValue("company", tenant.CompanyId);
            header.Parameters.AddWithValue("branch", tenant.BranchId);
            header.Parameters.AddWithValue("journal", journalId);
            await using var reader = await header.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var journal = new GeneralLedgerJournal(journalId, reader.GetInt64(0), reader.GetInt32(1),
                    reader.GetFieldValue<DateOnly>(2), reader.GetString(3).Trim(), reader.GetString(4));
                detail = new GeneralLedgerJournalDetail(journal, reader.GetString(5), reader.GetGuid(6),
                    reader.IsDBNull(7) ? null : reader.GetGuid(7),
                    reader.IsDBNull(8) ? null : reader.GetGuid(8),
                    Array.Empty<GeneralLedgerJournalLine>());
            }
        }

        if (detail is not null)
        {
            var lines = new List<GeneralLedgerJournalLine>();
            await using var command = new NpgsqlCommand("""
                SELECT line_number, account_id, description, debit_minor, credit_minor
                FROM monarch_gl_journal_lines
                WHERE company_id=@company AND branch_id=@branch AND journal_id=@journal
                ORDER BY line_number
                """, scope.Connection, scope.Transaction);
            command.Parameters.AddWithValue("company", tenant.CompanyId);
            command.Parameters.AddWithValue("branch", tenant.BranchId);
            command.Parameters.AddWithValue("journal", journalId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                lines.Add(new GeneralLedgerJournalLine(reader.GetInt32(0), reader.GetGuid(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetInt64(3), reader.GetInt64(4)));
            detail = detail with { Lines = lines };
        }

        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        return detail;
    }

    public async Task PostAsync(TenantScope tenant, Guid journalId, CancellationToken cancellationToken = default)
    {
        ValidateTenant(tenant);
        if (journalId == Guid.Empty) throw new ArgumentException("Journal ID is required.", nameof(journalId));
        await using var scope = await BeginAsync(tenant, cancellationToken);
        await using (var post = new NpgsqlCommand("""
            UPDATE monarch_gl_journals SET status='POSTED', posted_by=@user
            WHERE company_id=@company AND branch_id=@branch AND journal_id=@journal AND status='DRAFT'
            """, scope.Connection, scope.Transaction))
        {
            post.Parameters.AddWithValue("user", scope.Tenant.UserId);
            post.Parameters.AddWithValue("company", tenant.CompanyId);
            post.Parameters.AddWithValue("branch", tenant.BranchId);
            post.Parameters.AddWithValue("journal", journalId);
            if (await post.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                await using var alreadyPosted = new NpgsqlCommand("""
                    SELECT EXISTS(SELECT 1 FROM monarch_gl_journals
                      WHERE company_id=@company AND branch_id=@branch AND journal_id=@journal AND status='POSTED')
                    AND EXISTS(SELECT 1 FROM monarch_outbox
                      WHERE company_id=@company AND branch_id=@branch AND event_id=@event AND event_type='GeneralLedger.JournalPosted')
                    """, scope.Connection, scope.Transaction);
                alreadyPosted.Parameters.AddWithValue("company", tenant.CompanyId);
                alreadyPosted.Parameters.AddWithValue("branch", tenant.BranchId);
                alreadyPosted.Parameters.AddWithValue("journal", journalId);
                alreadyPosted.Parameters.AddWithValue("event", journalId);
                if ((bool)(await alreadyPosted.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? false))
                {
                    await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return;
                }
                throw new InvalidOperationException("Journal is missing, already posted, or cannot be posted.");
            }
        }
        await new PostgreSqlOutboxWriter().EnqueueAsync(scope, journalId, "GeneralLedger", journalId.ToString("D"),
            "GeneralLedger.JournalPosted", new { journalId, tenant.CompanyId, tenant.BranchId, userId = scope.Tenant.UserId },
            DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<GeneralLedgerTrialBalanceRow>> GetTrialBalanceAsync(
        TenantScope tenant, DateOnly asOfDate, string currencyCode, CancellationToken cancellationToken = default)
    {
        ValidateTenant(tenant);
        if (string.IsNullOrWhiteSpace(currencyCode) || currencyCode.Trim().Length != 3 || !currencyCode.Trim().All(char.IsAsciiLetter))
            throw new ArgumentException("An ISO 4217 currency code is required.", nameof(currencyCode));
        await using var scope = await BeginAsync(tenant, cancellationToken);
        const string sql = """
            SELECT a.account_id, a.account_code, a.account_name,
                   COALESCE(sum(l.debit_minor),0)::bigint AS debit_minor,
                   COALESCE(sum(l.credit_minor),0)::bigint AS credit_minor
            FROM monarch_gl_accounts a
            LEFT JOIN monarch_gl_journals j ON j.company_id=a.company_id
              AND j.branch_id=@branch AND j.status='POSTED' AND j.journal_date<=@as_of AND j.currency_code=@currency
            LEFT JOIN monarch_gl_journal_lines l ON l.company_id=j.company_id
              AND l.branch_id=j.branch_id AND l.journal_id=j.journal_id AND l.account_id=a.account_id
            WHERE a.company_id=@company
            GROUP BY a.account_id,a.account_code,a.account_name
            ORDER BY a.account_code
            """;
        await using var command = new NpgsqlCommand(sql, scope.Connection, scope.Transaction);
        command.Parameters.AddWithValue("company", tenant.CompanyId);
        command.Parameters.AddWithValue("branch", tenant.BranchId);
        command.Parameters.AddWithValue("as_of", asOfDate);
        command.Parameters.AddWithValue("currency", currencyCode.Trim().ToUpperInvariant());
        var rows = new List<GeneralLedgerTrialBalanceRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            rows.Add(new GeneralLedgerTrialBalanceRow(reader.GetGuid(0), reader.GetString(1), reader.GetString(2),
                reader.GetInt64(3), reader.GetInt64(4)));
        await reader.DisposeAsync().ConfigureAwait(false);
        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        return rows;
    }

    private ValueTask<TenantPostgreSqlTransaction> BeginAsync(TenantScope tenant, CancellationToken cancellationToken) =>
        TenantPostgreSqlTransaction.BeginAsync(factory,
            new TenantDatabaseContext(tenant.CompanyId, tenant.BranchId, tenant.UserId,
                tenant.IdentityIssuer, tenant.IdentitySubject, tenant.RequiredPermission), cancellationToken);

    private static void ValidateTenant(TenantScope tenant)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        if (tenant.CompanyId == Guid.Empty || tenant.BranchId == Guid.Empty)
            throw new ArgumentException("Company and branch scope are mandatory.", nameof(tenant));

        var identityFields = new[] { tenant.IdentityIssuer, tenant.IdentitySubject, tenant.RequiredPermission };
        var identityFieldCount = identityFields.Count(value => !string.IsNullOrWhiteSpace(value));
        if (identityFieldCount is not (0 or 3))
            throw new ArgumentException("OIDC issuer, subject and required permission must be supplied together.", nameof(tenant));
        if (identityFieldCount == 0 && tenant.UserId == Guid.Empty)
            throw new ArgumentException("A user scope is mandatory for non-federated operations.", nameof(tenant));
        if (identityFieldCount == 3 && tenant.UserId != Guid.Empty)
            throw new ArgumentException("Federated API scope must resolve its internal user from the OIDC subject.", nameof(tenant));
    }
}