using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Monarch.Api.Security;
using Monarch.Core.Abstractions;
using Monarch.Core.Accounting;
using Monarch.PostgreSql.Infrastructure;
using Monarch.Api.Services;
using Monarch.Api.Endpoints;
using Npgsql;
using System.Security.Claims;
using System.Security.Cryptography;


// JWT configuration must be absent in local mode or complete and cryptographically strong.
var noJwtServices = new ServiceCollection();
var noJwtConfiguration = new ConfigurationBuilder().AddInMemoryCollection().Build();
Assert(!JwtAuthenticationRegistration.AddFromConfiguration(noJwtServices, noJwtConfiguration),
    "JWT remains unconfigured in explicit local/demo mode");

var incompleteJwtRejected = false;
try
{
    var incompleteServices = new ServiceCollection();
    var incompleteConfiguration = new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string, string?> { [JwtAuthenticationRegistration.IssuerEnvironmentVariable] = "monarch-test" }).Build();
    JwtAuthenticationRegistration.AddFromConfiguration(incompleteServices, incompleteConfiguration);
}
catch (InvalidOperationException) { incompleteJwtRejected = true; }
Assert(incompleteJwtRejected, "partial JWT configuration fails closed at startup");

var weakJwtKeyRejected = false;
try
{
    var weakServices = new ServiceCollection();
    var weakConfiguration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        [JwtAuthenticationRegistration.IssuerEnvironmentVariable] = "monarch-test",
        [JwtAuthenticationRegistration.AudienceEnvironmentVariable] = "monarch-api",
        [JwtAuthenticationRegistration.SigningKeyEnvironmentVariable] = Convert.ToBase64String(new byte[16])
    }).Build();
    JwtAuthenticationRegistration.AddFromConfiguration(weakServices, weakConfiguration);
}
catch (InvalidOperationException) { weakJwtKeyRejected = true; }
Assert(weakJwtKeyRejected, "JWT signing keys shorter than 256 bits are rejected");

var jwtServices = new ServiceCollection();
jwtServices.AddLogging();
var jwtConfiguration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
{
    [JwtAuthenticationRegistration.IssuerEnvironmentVariable] = "monarch-test-issuer",
    [JwtAuthenticationRegistration.AudienceEnvironmentVariable] = "monarch-test-api",
    [JwtAuthenticationRegistration.SigningKeyEnvironmentVariable] =
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
}).Build();
Assert(JwtAuthenticationRegistration.AddFromConfiguration(jwtServices, jwtConfiguration),
    "complete JWT configuration registers bearer authentication");
using (var jwtProvider = jwtServices.BuildServiceProvider())
{
    var jwtOptions = jwtProvider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
        .Get(JwtBearerDefaults.AuthenticationScheme);
    Assert(jwtOptions.TokenValidationParameters.ValidateIssuer &&
        jwtOptions.TokenValidationParameters.ValidIssuer == "monarch-test-issuer" &&
        jwtOptions.TokenValidationParameters.ValidateAudience &&
        jwtOptions.TokenValidationParameters.ValidAudience == "monarch-test-api" &&
        jwtOptions.TokenValidationParameters.ValidateLifetime &&
        jwtOptions.TokenValidationParameters.RequireExpirationTime &&
        jwtOptions.TokenValidationParameters.ValidateIssuerSigningKey &&
        jwtOptions.TokenValidationParameters.ValidAlgorithms!.Contains(SecurityAlgorithms.HmacSha256),
        "JWT enforces issuer, audience, expiry, signature and HS256 algorithm validation");
}

// Validate a real signed OIDC ID-token proof against synthetic trusted discovery metadata.
using (var enrollmentRsa = System.Security.Cryptography.RSA.Create(2048))
{
    const string enrollmentIssuer = "https://issuer.enrollment.test";
    const string enrollmentClientId = "monarch-enrollment-client";
    const string enrollmentSubject = "enrollment-subject-001";
    const string enrollmentNonce = "nonce-value-created-by-the-invitation-service-012345";
    var enrollmentKey = new RsaSecurityKey(enrollmentRsa) { KeyId = "enrollment-test-key" };
    var enrollmentMetadata = new OpenIdConnectConfiguration { Issuer = enrollmentIssuer };
    enrollmentMetadata.SigningKeys.Add(enrollmentKey);
    var enrollmentJwtOptions = new JwtBearerOptions
    {
        ConfigurationManager = new FixedConfigurationManager<OpenIdConnectConfiguration>(enrollmentMetadata)
    };
    var enrollmentMonitor = new FixedOptionsMonitor<JwtBearerOptions>(enrollmentJwtOptions);
    var enrollmentConfiguration = new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string, string?>
        {
            [OidcEnrollmentProofValidator.ClientIdEnvironmentVariable] = enrollmentClientId
        }).Build();
    var enrollmentValidator = new OidcEnrollmentProofValidator(enrollmentMonitor, enrollmentConfiguration);
    var issued = DateTimeOffset.UtcNow;
    var signedIdToken = new JwtSecurityToken(
        enrollmentIssuer, enrollmentClientId,
        new[]
        {
            new Claim("sub", enrollmentSubject),
            new Claim("nonce", enrollmentNonce),
            new Claim("auth_time", issued.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new Claim("iat", issued.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture))
        },
        issued.AddMinutes(-1).UtcDateTime,
        issued.AddMinutes(2).UtcDateTime,
        new SigningCredentials(enrollmentKey, SecurityAlgorithms.RsaSha256));
    var signedIdTokenText = new JwtSecurityTokenHandler().WriteToken(signedIdToken);
    var validatedProof = await enrollmentValidator.ValidateAsync(signedIdTokenText);
    Assert(validatedProof.Issuer == enrollmentIssuer && validatedProof.Subject == enrollmentSubject &&
        validatedProof.Nonce == enrollmentNonce,
        "OIDC enrollment validator accepts a correctly signed, fresh ID token with exact issuer/audience/nonce");

    var wrongAudienceToken = new JwtSecurityToken(
        enrollmentIssuer, "different-client",
        signedIdToken.Claims,
        issued.AddMinutes(-1).UtcDateTime,
        issued.AddMinutes(2).UtcDateTime,
        new SigningCredentials(enrollmentKey, SecurityAlgorithms.RsaSha256));
    var wrongAudienceRejected = false;
    try { await enrollmentValidator.ValidateAsync(new JwtSecurityTokenHandler().WriteToken(wrongAudienceToken)); }
    catch (Microsoft.IdentityModel.Tokens.SecurityTokenException) { wrongAudienceRejected = true; }
    Assert(wrongAudienceRejected, "OIDC enrollment validator rejects an ID token issued for a different client audience");

    var wrongAuthorizedPartyToken = new JwtSecurityToken(
        enrollmentIssuer, enrollmentClientId,
        signedIdToken.Claims.Concat(new[] { new Claim("azp", "different-client") }),
        issued.AddMinutes(-1).UtcDateTime,
        issued.AddMinutes(2).UtcDateTime,
        new SigningCredentials(enrollmentKey, SecurityAlgorithms.RsaSha256));
    var wrongAuthorizedPartyRejected = false;
    try { await enrollmentValidator.ValidateAsync(new JwtSecurityTokenHandler().WriteToken(wrongAuthorizedPartyToken)); }
    catch (Microsoft.IdentityModel.Tokens.SecurityTokenException) { wrongAuthorizedPartyRejected = true; }
    Assert(wrongAuthorizedPartyRejected, "OIDC enrollment validator rejects a mismatched authorized-party claim even with a single audience");

    var staleClaims = signedIdToken.Claims.Where(claim => claim.Type != "auth_time").ToList();
    staleClaims.Add(new Claim("auth_time", issued.AddMinutes(-10).ToUnixTimeSeconds()
        .ToString(System.Globalization.CultureInfo.InvariantCulture)));
    var staleAuthenticationToken = new JwtSecurityToken(
        enrollmentIssuer, enrollmentClientId, staleClaims,
        issued.AddMinutes(-1).UtcDateTime, issued.AddMinutes(2).UtcDateTime,
        new SigningCredentials(enrollmentKey, SecurityAlgorithms.RsaSha256));
    var staleAuthenticationRejected = false;
    try { await enrollmentValidator.ValidateAsync(new JwtSecurityTokenHandler().WriteToken(staleAuthenticationToken)); }
    catch (Microsoft.IdentityModel.Tokens.SecurityTokenException) { staleAuthenticationRejected = true; }
    Assert(staleAuthenticationRejected, "OIDC enrollment validator rejects authentication older than five minutes");
}

// API application-boundary tests: tenant selection is untrusted and only OIDC iss/sub are accepted.
var boundaryStore = new RecordingGeneralLedgerStore();
var boundaryService = new GeneralLedgerApplicationService(boundaryStore);
var boundaryCompany = Guid.NewGuid();
var boundaryBranch = Guid.NewGuid();
const string boundaryIssuer = "https://identity.example.test";
const string boundarySubject = "oidc-subject-001";
var validBoundaryPrincipal = new ClaimsPrincipal(new ClaimsIdentity(new[]
{
    new Claim(GeneralLedgerApplicationService.IssuerClaim, boundaryIssuer),
    new Claim(GeneralLedgerApplicationService.SubjectClaim, boundarySubject),
    new Claim("monarch:permission", "gl.journal.post"),
    new Claim("monarch:user_id", Guid.NewGuid().ToString("D"))
}, "validated-test-authentication"));
await boundaryService.ListAccountsAsync(validBoundaryPrincipal, boundaryCompany, boundaryBranch);
Assert(boundaryStore.LastTenant == new TenantScope(
        boundaryCompany, boundaryBranch, Guid.Empty, boundaryIssuer, boundarySubject, "gl.accounts.read"),
    "GL application service derives OIDC identity from iss/sub and selects permission by operation");

await boundaryService.ListJournalsAsync(validBoundaryPrincipal, boundaryCompany, boundaryBranch);
Assert(boundaryStore.LastTenant?.RequiredPermission == "gl.journal.read" &&
    boundaryStore.LastTenant.UserId == Guid.Empty,
    "journal listing uses database-backed read permission and resolved OIDC identity");

var boundaryAccount = new GeneralLedgerAccount(Guid.NewGuid(), "1100", "Bank", "ASSET", "D");
await boundaryService.CreateAccountAsync(validBoundaryPrincipal, boundaryCompany, boundaryBranch, boundaryAccount);
Assert(boundaryStore.LastTenant?.RequiredPermission == "gl.accounts.manage" &&
    boundaryStore.LastTenant.UserId == Guid.Empty && boundaryStore.LastAccount == boundaryAccount,
    "GL account creation ignores token permission/user claims and delegates current RBAC to PostgreSQL");

await boundaryService.PostAsync(validBoundaryPrincipal, boundaryCompany, boundaryBranch, Guid.NewGuid());
Assert(boundaryStore.LastTenant?.RequiredPermission == "gl.journal.post",
    "journal posting carries its exact server-selected permission into the database transaction");

var endpointBuilder = WebApplication.CreateBuilder();
endpointBuilder.Services.AddAuthorization();
endpointBuilder.Services.AddScoped<IdentityEnrollmentApplicationService>(_ => null!);
endpointBuilder.Services.AddSingleton<OidcEnrollmentProofValidator>(_ => null!);
await using (var endpointApp = endpointBuilder.Build())
{
    endpointApp.MapGeneralLedgerEndpoints();
    endpointApp.MapIdentityEnrollmentEndpoints();
    var allRoutes = ((IEndpointRouteBuilder)endpointApp).DataSources.SelectMany(source => source.Endpoints)
        .OfType<RouteEndpoint>().ToArray();
    var glRoutes = allRoutes.Where(route => route.RoutePattern.RawText?.StartsWith("/api/v1/general-ledger/", StringComparison.Ordinal) == true).ToArray();
    Assert(glRoutes.Length == 7 && glRoutes.All(route =>
        route.Metadata.GetMetadata<IAuthorizeData>() is not null),
        "all seven GL HTTP routes carry authorization metadata");
    var enrollmentRoutes = allRoutes.Where(route =>
        route.RoutePattern.RawText?.StartsWith("/api/v1/security/identity-enrollments", StringComparison.Ordinal) == true).ToArray();
    var redeemRoute = enrollmentRoutes.Single(route => route.RoutePattern.RawText?.EndsWith("/redeem", StringComparison.Ordinal) == true);
    Assert(enrollmentRoutes.Length == 4 && redeemRoute.Metadata.GetMetadata<IAllowAnonymous>() is not null &&
        enrollmentRoutes.Where(route => !ReferenceEquals(route, redeemRoute))
            .All(route => route.Metadata.GetMetadata<IAuthorizeData>() is not null),
        "identity enrollment redemption is anonymous but invitation, review and approval routes require API authorization");
}
await ExpectUnauthorizedAsync(
    () => boundaryService.ListAccountsAsync(new ClaimsPrincipal(new ClaimsIdentity()), boundaryCompany, boundaryBranch),
    "GL application service rejects anonymous principals");

var ambiguousIssuerPrincipal = new ClaimsPrincipal(new ClaimsIdentity(new[]
{
    new Claim(GeneralLedgerApplicationService.IssuerClaim, boundaryIssuer),
    new Claim(GeneralLedgerApplicationService.IssuerClaim, "https://attacker.example.test"),
    new Claim(GeneralLedgerApplicationService.SubjectClaim, boundarySubject)
}, "validated-test-authentication"));
await ExpectUnauthorizedAsync(
    () => boundaryService.ListAccountsAsync(ambiguousIssuerPrincipal, boundaryCompany, boundaryBranch),
    "GL application service rejects ambiguous OIDC issuer claims");

var injectedPermissionPrincipal = new ClaimsPrincipal(new[]
{
    new ClaimsIdentity(new[]
    {
        new Claim(GeneralLedgerApplicationService.IssuerClaim, boundaryIssuer),
        new Claim(GeneralLedgerApplicationService.SubjectClaim, boundarySubject)
    }, "validated-test-authentication"),
    new ClaimsIdentity(new[]
    {
        new Claim("monarch:permission", "gl.accounts.manage"),
        new Claim(GeneralLedgerApplicationService.IssuerClaim, "https://untrusted.example.test")
    })
});
await boundaryService.ListAccountsAsync(injectedPermissionPrincipal, boundaryCompany, boundaryBranch);
Assert(boundaryStore.LastTenant?.RequiredPermission == "gl.accounts.read",
    "untrusted secondary identity and caller permission claims cannot elevate operation permissions");

var multipleAuthenticatedPrincipal = new ClaimsPrincipal(new[]
{
    new ClaimsIdentity(new[]
    {
        new Claim(GeneralLedgerApplicationService.IssuerClaim, boundaryIssuer),
        new Claim(GeneralLedgerApplicationService.SubjectClaim, boundarySubject)
    }, "first-authentication"),
    new ClaimsIdentity(new[]
    {
        new Claim(GeneralLedgerApplicationService.IssuerClaim, boundaryIssuer),
        new Claim(GeneralLedgerApplicationService.SubjectClaim, boundarySubject)
    }, "second-authentication")
});
await ExpectUnauthorizedAsync(
    () => boundaryService.ListAccountsAsync(multipleAuthenticatedPrincipal, boundaryCompany, boundaryBranch),
    "GL application service rejects multiple authenticated identities");

var connectionString = Environment.GetEnvironmentVariable(PostgreSqlOptions.ConnectionStringEnvironmentVariable)
    ?? throw new InvalidOperationException("MONARCH_POSTGRESQL_CONNECTION is required.");
var contextKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
await using var adminFactory = new PostgreSqlConnectionFactory(
    new PostgreSqlOptions(connectionString, requireTls: false, contextHmacKey: contextKey));
var migrator = new PostgreSqlSchemaMigrator(adminFactory);
await migrator.ApplyAsync();
await migrator.ApplyAsync();
await Task.WhenAll(migrator.ApplyAsync(), migrator.ApplyAsync());

await using (var admin = await adminFactory.OpenConnectionAsync())
{
    await using var provisionSecret = new NpgsqlCommand("""
        INSERT INTO monarch_security.context_secrets(secret_id,secret)
        VALUES(1,decode(@key,'hex'))
        ON CONFLICT(secret_id) DO UPDATE SET secret=EXCLUDED.secret
        """, admin);
    provisionSecret.Parameters.AddWithValue("key", contextKey);
    await provisionSecret.ExecuteNonQueryAsync();

    await using var verifyHashes = new NpgsqlCommand(
        "SELECT count(*) FROM monarch_schema_migrations WHERE length(btrim(sha256))=64", admin);
    Assert(Convert.ToInt32(await verifyHashes.ExecuteScalarAsync()) >= 2,
        "ordered migrations recorded with SHA-256 checksums");

    await using var readHash = new NpgsqlCommand(
        "SELECT btrim(sha256) FROM monarch_schema_migrations WHERE version='001_central_tenancy'", admin);
    var originalHash = (string)(await readHash.ExecuteScalarAsync()
        ?? throw new InvalidOperationException("Missing migration hash."));
    await using (var corruptHash = new NpgsqlCommand(
        "UPDATE monarch_schema_migrations SET sha256=repeat('0',64) WHERE version='001_central_tenancy'", admin))
        await corruptHash.ExecuteNonQueryAsync();

    var checksumMismatchRejected = false;
    try { await migrator.ApplyAsync(); }
    catch (InvalidOperationException) { checksumMismatchRejected = true; }

    await using (var restoreHash = new NpgsqlCommand(
        "UPDATE monarch_schema_migrations SET sha256=@hash WHERE version='001_central_tenancy'", admin))
    {
        restoreHash.Parameters.AddWithValue("hash", originalHash);
        await restoreHash.ExecuteNonQueryAsync();
    }
    Assert(checksumMismatchRejected, "applied migration checksum tampering is rejected");
}
await migrator.ApplyAsync();

var runtimePassword = Guid.NewGuid().ToString("N");
await using (var admin = await adminFactory.OpenConnectionAsync())
{
    await using var createRole = new NpgsqlCommand(
        $"CREATE ROLE monarch_runtime_ci LOGIN NOSUPERUSER NOBYPASSRLS PASSWORD '{runtimePassword}'", admin);
    await createRole.ExecuteNonQueryAsync();

    await using var grants = new NpgsqlCommand("""
        GRANT USAGE ON SCHEMA public, monarch_security TO monarch_runtime_ci;
        GRANT monarch_api_runtime TO monarch_runtime_ci;
        GRANT SELECT ON ALL TABLES IN SCHEMA public TO monarch_runtime_ci;
        GRANT INSERT ON monarch_outbox, monarch_gl_accounts, monarch_gl_journals,
          monarch_gl_journal_lines, monarch_gl_journal_sequences TO monarch_runtime_ci;
        GRANT UPDATE ON monarch_gl_journals, monarch_gl_journal_lines, monarch_gl_journal_sequences TO monarch_runtime_ci;
        GRANT EXECUTE ON FUNCTION monarch_gl_next_journal_number(uuid,uuid,integer) TO monarch_runtime_ci;
        GRANT EXECUTE ON FUNCTION monarch_security.set_tenant_context(uuid,uuid,uuid,bigint,text) TO monarch_runtime_ci;
        GRANT EXECUTE ON FUNCTION monarch_security.context_is_valid() TO monarch_runtime_ci;
        """, admin);
    await grants.ExecuteNonQueryAsync();
}

var runtimeBuilder = new NpgsqlConnectionStringBuilder(connectionString)
{
    Username = "monarch_runtime_ci",
    Password = runtimePassword,
    Pooling = true,
    MinPoolSize = 1,
    MaxPoolSize = 1
};
await using var runtimeFactory = new PostgreSqlConnectionFactory(
    new PostgreSqlOptions(runtimeBuilder.ConnectionString, requireTls: false, contextHmacKey: contextKey));

var first = await SeedTenantAsync(adminFactory, "FIRST");
var second = await SeedTenantAsync(adminFactory, "SECOND");
var federatedSeed = await SeedTenantAsync(adminFactory, "FED");
const string testIssuer = "https://identity.monarch-ci.example";
const string testSubject = "oidc-subject-first";
await SeedOidcAuthorizationAsync(adminFactory, federatedSeed.Context, testIssuer, testSubject,
    "gl.accounts.manage", "security.identity.invite", "security.identity.enrollment.approve");
var enrollmentTarget = await AddUserToTenantAsync(adminFactory, federatedSeed.Context);
const string enrollmentTargetExistingSubject = "enrollment-target-existing";
await SeedOidcAuthorizationAsync(adminFactory, enrollmentTarget, testIssuer, enrollmentTargetExistingSubject,
    "security.identity.enrollment.approve");
var enrollmentApprover = await AddUserToTenantAsync(adminFactory, federatedSeed.Context);
const string identityEnrollmentApproverSubject = "oidc-enrollment-approver";
await SeedOidcAuthorizationAsync(adminFactory, enrollmentApprover, testIssuer, identityEnrollmentApproverSubject,
    "security.identity.enrollment.approve", "gl.accounts.manage");
var auditUpdateRejected = false;
await using (var auditAdmin = await adminFactory.OpenConnectionAsync())
{
    await using var mutateAudit = new NpgsqlCommand("""
        UPDATE monarch_security.authorization_audit
        SET database_actor='tampered'
        WHERE audit_id=(SELECT audit_id FROM monarch_security.authorization_audit LIMIT 1)
        """, auditAdmin);
    try { await mutateAudit.ExecuteNonQueryAsync(); }
    catch (PostgresException exception) when (exception.SqlState == "55000") { auditUpdateRejected = true; }
}
Assert(auditUpdateRejected, "authorization audit rejects UPDATE even from a privileged SQL session while append-only trigger is active");
var auditTruncateRejected = false;
await using (var auditAdmin = await adminFactory.OpenConnectionAsync())
{
    await using var truncateAudit = new NpgsqlCommand("TRUNCATE monarch_security.authorization_audit", auditAdmin);
    try { await truncateAudit.ExecuteNonQueryAsync(); }
    catch (PostgresException exception) when (exception.SqlState == "55000") { auditTruncateRejected = true; }
}
Assert(auditTruncateRejected, "authorization audit rejects TRUNCATE while append-only trigger is active");

var federatedTenant = new TenantScope(federatedSeed.Context.CompanyId, federatedSeed.Context.BranchId, Guid.Empty,
    testIssuer, testSubject, "gl.accounts.manage");
await using (var authorizedScope = await TenantPostgreSqlTransaction.BeginAsync(runtimeFactory,
    new TenantDatabaseContext(federatedSeed.Context.CompanyId, federatedSeed.Context.BranchId, Guid.Empty,
        testIssuer, testSubject, "gl.accounts.manage")))
{
    Assert(authorizedScope.Tenant.UserId == federatedSeed.Context.UserId,
        "PostgreSQL resolves OIDC issuer/subject to the internal tenant user");
    await authorizedScope.CommitAsync();
}
var deniedPermission = false;
try
{
    await using var deniedScope = await TenantPostgreSqlTransaction.BeginAsync(runtimeFactory,
        new TenantDatabaseContext(federatedSeed.Context.CompanyId, federatedSeed.Context.BranchId, Guid.Empty,
            testIssuer, testSubject, "gl.journal.post"));
}
catch (UnauthorizedAccessException) { deniedPermission = true; }
Assert(deniedPermission, "database RBAC rejects an OIDC user without the requested journal-post permission");

// One-time OIDC identity enrollment: invitation secret and nonce are hashed at rest.
var invitationSecretBytes = RandomNumberGenerator.GetBytes(32);
var invitationSecret = Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Encode(invitationSecretBytes);
var invitationTokenHash = SHA256.HashData(invitationSecretBytes);
CryptographicOperations.ZeroMemory(invitationSecretBytes);
var invitationNonce = Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
var invitationNonceHash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(invitationNonce));
Guid enrollmentId;
await using (var inviterScope = await TenantPostgreSqlTransaction.BeginAsync(runtimeFactory,
    new TenantDatabaseContext(federatedSeed.Context.CompanyId, federatedSeed.Context.BranchId, Guid.Empty,
        testIssuer, testSubject, "security.identity.invite")))
await using (var createEnrollment = new NpgsqlCommand("""
    SELECT monarch_security.create_identity_enrollment_claim(
        @company,@branch,@target,@issuer,@token_hash,@nonce_hash,300,'CI OIDC ownership proof')
    """, inviterScope.Connection, inviterScope.Transaction))
{
    createEnrollment.Parameters.AddWithValue("company", federatedSeed.Context.CompanyId);
    createEnrollment.Parameters.AddWithValue("branch", federatedSeed.Context.BranchId);
    createEnrollment.Parameters.AddWithValue("target", enrollmentTarget.UserId);
    createEnrollment.Parameters.AddWithValue("issuer", testIssuer);
    createEnrollment.Parameters.AddWithValue("token_hash", invitationTokenHash);
    createEnrollment.Parameters.AddWithValue("nonce_hash", invitationNonceHash);
    enrollmentId = (Guid)(await createEnrollment.ExecuteScalarAsync()
        ?? throw new InvalidOperationException("Identity enrollment claim was not created."));
    await inviterScope.CommitAsync();
}
var wrongNonceRejected = false;
try
{
    await using var redeemConnection = await runtimeFactory.OpenConnectionAsync();
    await using var wrongNonce = new NpgsqlCommand("""
        SELECT enrollment_id FROM monarch_security.redeem_identity_enrollment_claim(
            @token_hash,@issuer,@subject,'wrong-nonce-value-that-is-long-enough')
        """, redeemConnection);
    wrongNonce.Parameters.AddWithValue("token_hash", invitationTokenHash);
    wrongNonce.Parameters.AddWithValue("issuer", testIssuer);
    wrongNonce.Parameters.AddWithValue("subject", "new-oidc-subject");
    await wrongNonce.ExecuteScalarAsync();
}
catch (PostgresException exception) when (exception.SqlState == "28000") { wrongNonceRejected = true; }
Assert(wrongNonceRejected, "OIDC enrollment rejects a nonce that does not match the stored challenge");
var wrongIssuerRejected = false;
try
{
    await using var redeemConnection = await runtimeFactory.OpenConnectionAsync();
    await using var wrongIssuer = new NpgsqlCommand("""
        SELECT enrollment_id FROM monarch_security.redeem_identity_enrollment_claim(
            @token_hash,'https://attacker.identity.example','new-oidc-subject',@nonce)
        """, redeemConnection);
    wrongIssuer.Parameters.AddWithValue("token_hash", invitationTokenHash);
    wrongIssuer.Parameters.AddWithValue("nonce", invitationNonce);
    await wrongIssuer.ExecuteScalarAsync();
}
catch (PostgresException exception) when (exception.SqlState == "28000") { wrongIssuerRejected = true; }
Assert(wrongIssuerRejected, "OIDC enrollment rejects a proof from an issuer other than the invited provider");

await using (var redeemConnection = await runtimeFactory.OpenConnectionAsync())
await using (var redeem = new NpgsqlCommand("""
    SELECT enrollment_id FROM monarch_security.redeem_identity_enrollment_claim(
        @token_hash,@issuer,@subject,@nonce)
    """, redeemConnection))
{
    redeem.Parameters.AddWithValue("token_hash", invitationTokenHash);
    redeem.Parameters.AddWithValue("issuer", testIssuer);
    redeem.Parameters.AddWithValue("subject", "new-oidc-subject");
    redeem.Parameters.AddWithValue("nonce", invitationNonce);
    Assert((Guid)(await redeem.ExecuteScalarAsync() ?? Guid.Empty) == enrollmentId,
        "valid OIDC issuer/subject/nonce proof redeems the exact invitation");
}
var replayRejected = false;
try
{
    await using var replayConnection = await runtimeFactory.OpenConnectionAsync();
    await using var replay = new NpgsqlCommand("""
        SELECT enrollment_id FROM monarch_security.redeem_identity_enrollment_claim(
            @token_hash,@issuer,@subject,@nonce)
        """, replayConnection);
    replay.Parameters.AddWithValue("token_hash", invitationTokenHash);
    replay.Parameters.AddWithValue("issuer", testIssuer);
    replay.Parameters.AddWithValue("subject", "new-oidc-subject");
    replay.Parameters.AddWithValue("nonce", invitationNonce);
    await replay.ExecuteScalarAsync();
}
catch (PostgresException exception) when (exception.SqlState == "28000") { replayRejected = true; }
Assert(replayRejected, "OIDC enrollment invitation is single-use and replay is rejected");

var duplicateLiveInvitationRejected = false;
try
{
    var secondSecret = RandomNumberGenerator.GetBytes(32);
    var secondNonce = Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
    await using var inviterScope = await TenantPostgreSqlTransaction.BeginAsync(runtimeFactory,
        new TenantDatabaseContext(federatedSeed.Context.CompanyId, federatedSeed.Context.BranchId, Guid.Empty,
            testIssuer, testSubject, "security.identity.invite"));
    await using var duplicateInvite = new NpgsqlCommand("""
        SELECT monarch_security.create_identity_enrollment_claim(
            @company,@branch,@target,@issuer,@token_hash,@nonce_hash,300,'Duplicate live invitation')
        """, inviterScope.Connection, inviterScope.Transaction);
    duplicateInvite.Parameters.AddWithValue("company", federatedSeed.Context.CompanyId);
    duplicateInvite.Parameters.AddWithValue("branch", federatedSeed.Context.BranchId);
    duplicateInvite.Parameters.AddWithValue("target", enrollmentTarget.UserId);
    duplicateInvite.Parameters.AddWithValue("issuer", testIssuer);
    duplicateInvite.Parameters.AddWithValue("token_hash", SHA256.HashData(secondSecret));
    duplicateInvite.Parameters.AddWithValue("nonce_hash", SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secondNonce)));
    await duplicateInvite.ExecuteScalarAsync();
    await inviterScope.CommitAsync();
}
catch (PostgresException exception) when (exception.SqlState == "23505") { duplicateLiveInvitationRejected = true; }
Assert(duplicateLiveInvitationRejected, "database prevents multiple live OIDC enrollment invitations for the same target and issuer");

var inviterSelfApprovalRejected = false;
try
{
    await using var inviterScope = await TenantPostgreSqlTransaction.BeginAsync(runtimeFactory,
        new TenantDatabaseContext(federatedSeed.Context.CompanyId, federatedSeed.Context.BranchId, Guid.Empty,
            testIssuer, testSubject, "security.identity.enrollment.approve"));
    await using var selfApprove = new NpgsqlCommand(
        "SELECT monarch_security.approve_identity_enrollment_claim(@id,'self approval attempt')",
        inviterScope.Connection, inviterScope.Transaction);
    selfApprove.Parameters.AddWithValue("id", enrollmentId);
    await selfApprove.ExecuteNonQueryAsync();
    await inviterScope.CommitAsync();
}
catch (UnauthorizedAccessException) { inviterSelfApprovalRejected = true; }
catch (PostgresException exception) when (exception.SqlState == "28000") { inviterSelfApprovalRejected = true; }
Assert(inviterSelfApprovalRejected, "identity invitation creator cannot approve their own enrollment");

var targetSelfApprovalRejected = false;
try
{
    await using var targetScope = await TenantPostgreSqlTransaction.BeginAsync(runtimeFactory,
        new TenantDatabaseContext(enrollmentTarget.CompanyId, enrollmentTarget.BranchId, Guid.Empty,
            testIssuer, enrollmentTargetExistingSubject, "security.identity.enrollment.approve"));
    await using var targetApprove = new NpgsqlCommand(
        "SELECT monarch_security.approve_identity_enrollment_claim(@id,'target self approval attempt')",
        targetScope.Connection, targetScope.Transaction);
    targetApprove.Parameters.AddWithValue("id", enrollmentId);
    await targetApprove.ExecuteNonQueryAsync();
    await targetScope.CommitAsync();
}
catch (PostgresException exception) when (exception.SqlState == "28000") { targetSelfApprovalRejected = true; }
Assert(targetSelfApprovalRejected, "identity enrollment target cannot approve their own OIDC account link");

await using (var approverScope = await TenantPostgreSqlTransaction.BeginAsync(runtimeFactory,
    new TenantDatabaseContext(enrollmentApprover.CompanyId, enrollmentApprover.BranchId, Guid.Empty,
        testIssuer, identityEnrollmentApproverSubject, "security.identity.enrollment.approve")))
await using (var approveEnrollment = new NpgsqlCommand(
    "SELECT monarch_security.approve_identity_enrollment_claim(@id,'independent CI approval')",
    approverScope.Connection, approverScope.Transaction))
{
    approveEnrollment.Parameters.AddWithValue("id", enrollmentId);
    await approveEnrollment.ExecuteNonQueryAsync();
    await approverScope.CommitAsync();
}
await using (var verifyEnrollment = await adminFactory.OpenConnectionAsync())
await using (var verifyMapping = new NpgsqlCommand("""
    SELECT count(*) FROM monarch_security.external_identities
    WHERE company_id=@company AND user_id=@target AND issuer=@issuer
      AND subject='new-oidc-subject' AND is_active
    """, verifyEnrollment))
{
    verifyMapping.Parameters.AddWithValue("company", federatedSeed.Context.CompanyId);
    verifyMapping.Parameters.AddWithValue("target", enrollmentTarget.UserId);
    verifyMapping.Parameters.AddWithValue("issuer", testIssuer);
    Assert(Convert.ToInt32(await verifyMapping.ExecuteScalarAsync()) == 1,
        "independent approver atomically activates only the OIDC identity proven by the target user");
}
await using (var verifyClaim = await adminFactory.OpenConnectionAsync())
await using (var storedHash = new NpgsqlCommand("""
    SELECT status, octet_length(token_hash), octet_length(nonce_hash)
    FROM monarch_security.identity_enrollment_claims WHERE enrollment_id=@id
    """, verifyClaim))
{
    storedHash.Parameters.AddWithValue("id", enrollmentId);
    await using var reader = await storedHash.ExecuteReaderAsync();
    await reader.ReadAsync();
    Assert(reader.GetString(0) == "APPROVED" && reader.GetInt32(1) == 32 && reader.GetInt32(2) == 32,
        "only fixed-length token and nonce hashes remain stored after approved enrollment");
}

var writer = new PostgreSqlOutboxWriter();

await using (var scope = await TenantPostgreSqlTransaction.BeginAsync(runtimeFactory, first.Context))
{
    Assert(await writer.EnqueueAsync(scope, Guid.NewGuid(), "Smoke", "aggregate-1",
        "Smoke.Created", new { tenant = "first" }, DateTimeOffset.UtcNow), "first tenant event inserted");
    var eventId = Guid.NewGuid();
    Assert(await writer.EnqueueAsync(scope, eventId, "Smoke", "aggregate-2",
        "Smoke.Created", new { value = 2 }, DateTimeOffset.UtcNow), "idempotency event inserted");
    Assert(!await writer.EnqueueAsync(scope, eventId, "Smoke", "aggregate-2",
        "Smoke.Created", new { value = 2 }, DateTimeOffset.UtcNow), "duplicate event ignored");
    await scope.CommitAsync();
}
await using (var scope = await TenantPostgreSqlTransaction.BeginAsync(runtimeFactory, second.Context))
{
    Assert(await writer.EnqueueAsync(scope, Guid.NewGuid(), "Smoke", "aggregate-3",
        "Smoke.Created", new { tenant = "second" }, DateTimeOffset.UtcNow), "second tenant event inserted");
    await scope.CommitAsync();
}
await using (var scope = await TenantPostgreSqlTransaction.BeginAsync(runtimeFactory, first.Context))
await using (var command = new NpgsqlCommand("SELECT count(*) FROM monarch_outbox", scope.Connection, scope.Transaction))
    Assert(Convert.ToInt64(await command.ExecuteScalarAsync()) == 2, "RLS hides the other tenant's outbox rows");

// Reuse the same pooled connection; transaction-local tenant settings must be cleared on commit.
await using (var connection = await runtimeFactory.OpenConnectionAsync())
await using (var command = new NpgsqlCommand("""
    SELECT COALESCE(current_setting('monarch.company_id', true),''),
           COALESCE(current_setting('monarch.branch_id', true),''),
           COALESCE(current_setting('monarch.user_id', true),''),
           COALESCE(current_setting('monarch.context_issued_at', true),''),
           COALESCE(current_setting('monarch.context_signature', true),'')
    """, connection))
await using (var reader = await command.ExecuteReaderAsync())
{
    await reader.ReadAsync();
    Assert(Enumerable.Range(0, 5).All(i => string.IsNullOrEmpty(reader.GetString(i))),
        "tenant context does not leak through the connection pool");
}

// Custom GUCs are forgeable; the signed HMAC context required by RLS is not.
await using (var connection = await runtimeFactory.OpenConnectionAsync())
await using (var transaction = await connection.BeginTransactionAsync())
{
    await using var forge = new NpgsqlCommand("""
        SELECT set_config('monarch.company_id',@c,true),
               set_config('monarch.branch_id',@b,true),
               set_config('monarch.user_id',@u,true),
               set_config('monarch.context_issued_at',@issued,true),
               set_config('monarch.context_signature',@sig,true)
        """, connection, transaction);
    forge.Parameters.AddWithValue("c", first.Context.CompanyId.ToString("D"));
    forge.Parameters.AddWithValue("b", first.Context.BranchId.ToString("D"));
    forge.Parameters.AddWithValue("u", first.Context.UserId.ToString("D"));
    forge.Parameters.AddWithValue("issued", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString());
    forge.Parameters.AddWithValue("sig", new string('0', 64));
    await forge.ExecuteNonQueryAsync();
    await using var hidden = new NpgsqlCommand("SELECT count(*) FROM monarch_outbox", connection, transaction);
    Assert(Convert.ToInt64(await hidden.ExecuteScalarAsync()) == 0, "unsigned tenant GUC values cannot bypass RLS");
    await transaction.RollbackAsync();
}

var unknownTenantDenied = false;
try
{
    await using var ignored = await TenantPostgreSqlTransaction.BeginAsync(runtimeFactory,
        new TenantDatabaseContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()));
}
catch (UnauthorizedAccessException) { unknownTenantDenied = true; }
Assert(unknownTenantDenied, "unknown tenant membership rejected");

// Membership lifecycle is privileged and audited.
await using (var admin = await adminFactory.OpenConnectionAsync())
{
    await using var revoke = new NpgsqlCommand(
        "SELECT monarch_security.revoke_branch_access(@c,@b,@u,@actor,'integration test')", admin);
    revoke.Parameters.AddWithValue("c", first.Context.CompanyId);
    revoke.Parameters.AddWithValue("b", first.Context.BranchId);
    revoke.Parameters.AddWithValue("u", first.Context.UserId);
    revoke.Parameters.AddWithValue("actor", first.Context.UserId);
    Assert((bool)(await revoke.ExecuteScalarAsync() ?? false), "membership revocation accepted by privileged function");
}
var revokedDenied = false;
try { await using var ignored = await TenantPostgreSqlTransaction.BeginAsync(runtimeFactory, first.Context); }
catch (UnauthorizedAccessException) { revokedDenied = true; }
Assert(revokedDenied, "revoked membership immediately blocks new tenant transactions");

var membershipWriteDenied = false;
try
{
    await using var scope = await TenantPostgreSqlTransaction.BeginAsync(runtimeFactory, second.Context);
    await using var forbidden = new NpgsqlCommand("""
        UPDATE monarch_user_branch_grants SET is_active=false
        WHERE company_id=@c AND branch_id=@b AND user_id=@u
        """, scope.Connection, scope.Transaction);
    forbidden.Parameters.AddWithValue("c", second.Context.CompanyId);
    forbidden.Parameters.AddWithValue("b", second.Context.BranchId);
    forbidden.Parameters.AddWithValue("u", second.Context.UserId);
    await forbidden.ExecuteNonQueryAsync();
}
catch (PostgresException) { membershipWriteDenied = true; }
Assert(membershipWriteDenied, "runtime role cannot mutate its own membership");

await using (var admin = await adminFactory.OpenConnectionAsync())
{
    await using var grant = new NpgsqlCommand(
        "SELECT monarch_security.grant_branch_access(@c,@b,@u,@actor,'regrant test')", admin);
    grant.Parameters.AddWithValue("c", first.Context.CompanyId);
    grant.Parameters.AddWithValue("b", first.Context.BranchId);
    grant.Parameters.AddWithValue("u", first.Context.UserId);
    grant.Parameters.AddWithValue("actor", first.Context.UserId);
    await grant.ExecuteNonQueryAsync();

    await using var audit = new NpgsqlCommand("""
        SELECT count(*) FROM monarch_membership_audit
        WHERE company_id=@c AND branch_id=@b AND user_id=@u
        """, admin);
    audit.Parameters.AddWithValue("c", first.Context.CompanyId);
    audit.Parameters.AddWithValue("b", first.Context.BranchId);
    audit.Parameters.AddWithValue("u", first.Context.UserId);
    Assert(Convert.ToInt32(await audit.ExecuteScalarAsync()) == 2, "grant/revoke history retained");
}

// General Ledger: integer minor units, balanced posting, transactional outbox and trial balance.
var tenant = new TenantScope(first.Context.CompanyId, first.Context.BranchId, first.Context.UserId);
var gl = new PostgreSqlGeneralLedgerStore(runtimeFactory);
var federatedCash = Guid.NewGuid();
await gl.CreateAccountAsync(federatedTenant,
    new GeneralLedgerAccount(federatedCash, "1099", "Federated Cash", "ASSET", "D"));
await using (var federatedReadScope = await TenantPostgreSqlTransaction.BeginAsync(runtimeFactory,
    new TenantDatabaseContext(federatedSeed.Context.CompanyId, federatedSeed.Context.BranchId, Guid.Empty,
        testIssuer, testSubject, "gl.accounts.manage")))
{
    Assert(federatedReadScope.Tenant.UserId == federatedSeed.Context.UserId,
        "federated GL write uses database-resolved actor identity");
    await federatedReadScope.CommitAsync();
}
await using (var actorAuditScope = await TenantPostgreSqlTransaction.BeginAsync(runtimeFactory, federatedSeed.Context))
await using (var actorAudit = new NpgsqlCommand("""
    SELECT payload->>'actorUserId' FROM monarch_outbox
    WHERE event_type='GeneralLedger.AccountCreated' AND aggregate_id=@account
    """, actorAuditScope.Connection, actorAuditScope.Transaction))
{
    actorAudit.Parameters.AddWithValue("account", federatedCash.ToString("D"));
    Assert((string?)await actorAudit.ExecuteScalarAsync() == federatedSeed.Context.UserId.ToString("D"),
        "federated GL outbox records the database-resolved internal actor, never an empty or token-supplied user");
}
await using (var admin = await adminFactory.OpenConnectionAsync())
await using (var revokeRole = new NpgsqlCommand("""
    UPDATE monarch_security.user_roles
    SET is_active=false, revoked_at=now(), revoked_by=@actor
    WHERE company_id=@company AND user_id=@user AND is_active
    """, admin))
{
    revokeRole.Parameters.AddWithValue("actor", federatedSeed.Context.UserId);
    revokeRole.Parameters.AddWithValue("company", federatedSeed.Context.CompanyId);
    revokeRole.Parameters.AddWithValue("user", federatedSeed.Context.UserId);
    Assert(await revokeRole.ExecuteNonQueryAsync() == 1, "administrator revokes the OIDC user's role assignment");
}
var revokedPermissionDenied = false;
try
{
    await using var revokedScope = await TenantPostgreSqlTransaction.BeginAsync(runtimeFactory,
        new TenantDatabaseContext(federatedSeed.Context.CompanyId, federatedSeed.Context.BranchId, Guid.Empty,
            testIssuer, testSubject, "gl.accounts.manage"));
}
catch (UnauthorizedAccessException) { revokedPermissionDenied = true; }
Assert(revokedPermissionDenied, "revoked RBAC assignment blocks the next OIDC-protected database transaction");

// Controlled identity provisioning: requester and approver are distinct OIDC principals.
var provisioningSeed = await SeedTenantAsync(adminFactory, "PROV");
var provisioningRequester = provisioningSeed.Context;
var provisioningApprover = await AddUserToTenantAsync(adminFactory, provisioningSeed.Context);
var provisioningTarget = await AddUserToTenantAsync(adminFactory, provisioningSeed.Context);
const string requesterIssuer = "https://identity.monarch-ci.example";
const string requesterSubject = "provisioning-requester";
const string approverIssuer = "https://identity.monarch-ci.example";
const string provisioningApproverSubject = "provisioning-approver";
const string targetIssuer = "https://identity.monarch-ci.example";
const string targetSubject = "provisioning-target";
await SeedOidcAuthorizationAsync(adminFactory, provisioningRequester, requesterIssuer, requesterSubject,
    "security.provisioning.request", "security.provisioning.approve");
await SeedOidcAuthorizationAsync(adminFactory, provisioningApprover, approverIssuer, provisioningApproverSubject,
    "security.provisioning.approve", "gl.accounts.manage");

var targetRoleId = Guid.NewGuid();
var elevatedRoleId = Guid.NewGuid();
await using (var provisionAdmin = await adminFactory.OpenConnectionAsync())
await using (var provisionTransaction = await provisionAdmin.BeginTransactionAsync())
{
    await ExecuteAsync(provisionAdmin, provisionTransaction,
        "INSERT INTO monarch_security.roles(company_id,role_id,role_code,role_name) VALUES (@company,@role,'CI-TARGET','CI target role')",
        ("company", provisioningSeed.Context.CompanyId), ("role", targetRoleId));
    await ExecuteAsync(provisionAdmin, provisionTransaction,
        "INSERT INTO monarch_security.role_permissions(company_id,role_id,permission_code) VALUES (@company,@role,'gl.accounts.manage')",
        ("company", provisioningSeed.Context.CompanyId), ("role", targetRoleId));
    await ExecuteAsync(provisionAdmin, provisionTransaction,
        "INSERT INTO monarch_security.permissions(permission_code,description) VALUES ('security.root','CI escalation sentinel') ON CONFLICT(permission_code) DO NOTHING");
    await ExecuteAsync(provisionAdmin, provisionTransaction,
        "INSERT INTO monarch_security.roles(company_id,role_id,role_code,role_name) VALUES (@company,@role,'CI-ELEVATED','CI elevated role')",
        ("company", provisioningSeed.Context.CompanyId), ("role", elevatedRoleId));
    await ExecuteAsync(provisionAdmin, provisionTransaction,
        "INSERT INTO monarch_security.role_permissions(company_id,role_id,permission_code) VALUES (@company,@role,'security.provisioning.approve'),(@company,@role,'security.root')",
        ("company", provisioningSeed.Context.CompanyId), ("role", elevatedRoleId));
    await provisionTransaction.CommitAsync();
}

// Identity linking is fail-closed until the target user proves control of the OIDC identity.
var unprovenIdentityLinkRejected = false;
try
{
    await using var requesterScope = await TenantPostgreSqlTransaction.BeginAsync(runtimeFactory,
        new TenantDatabaseContext(provisioningSeed.Context.CompanyId, provisioningSeed.Context.BranchId, Guid.Empty,
            requesterIssuer, requesterSubject, "security.provisioning.request"));
    await using var request = new NpgsqlCommand("""
        SELECT monarch_security.request_provisioning_change(
            @company,@branch,'LINK_IDENTITY',@target,@issuer,@subject,NULL,'Unproven identity link attempt')
        """, requesterScope.Connection, requesterScope.Transaction);
    request.Parameters.AddWithValue("company", provisioningSeed.Context.CompanyId);
    request.Parameters.AddWithValue("branch", provisioningSeed.Context.BranchId);
    request.Parameters.AddWithValue("target", provisioningTarget.UserId);
    request.Parameters.AddWithValue("issuer", targetIssuer);
    request.Parameters.AddWithValue("subject", targetSubject);
    await request.ExecuteScalarAsync();
    await requesterScope.CommitAsync();
}
catch (PostgresException exception) when (exception.SqlState == "55000")
{
    unprovenIdentityLinkRejected = true;
}
Assert(unprovenIdentityLinkRejected,
    "database rejects OIDC identity linking until target-user ownership proof is implemented");

Guid assignRequestId;
await using (var requesterScope = await TenantPostgreSqlTransaction.BeginAsync(runtimeFactory,
    new TenantDatabaseContext(provisioningSeed.Context.CompanyId, provisioningSeed.Context.BranchId, Guid.Empty,
        requesterIssuer, requesterSubject, "security.provisioning.request")))
await using (var request = new NpgsqlCommand("""
    SELECT monarch_security.request_provisioning_change(
        @company,@branch,'ASSIGN_ROLE',@target,NULL,NULL,@role,'Assign least-privilege GL role')
    """, requesterScope.Connection, requesterScope.Transaction))
{
    request.Parameters.AddWithValue("company", provisioningSeed.Context.CompanyId);
    request.Parameters.AddWithValue("branch", provisioningSeed.Context.BranchId);
    request.Parameters.AddWithValue("target", provisioningTarget.UserId);
    request.Parameters.AddWithValue("role", targetRoleId);
    assignRequestId = (Guid)(await request.ExecuteScalarAsync()
        ?? throw new InvalidOperationException("Role assignment request returned no ID."));
    await requesterScope.CommitAsync();
}
await using (var approverScope = await TenantPostgreSqlTransaction.BeginAsync(runtimeFactory,
    new TenantDatabaseContext(provisioningSeed.Context.CompanyId, provisioningSeed.Context.BranchId, Guid.Empty,
        approverIssuer, provisioningApproverSubject, "security.provisioning.approve")))
await using (var approve = new NpgsqlCommand(
    "SELECT monarch_security.approve_provisioning_change(@request,'least-privilege role approved')",
    approverScope.Connection, approverScope.Transaction))
{
    approve.Parameters.AddWithValue("request", assignRequestId);
    await approve.ExecuteNonQueryAsync();
    await approverScope.CommitAsync();
}

var unlinkedTargetIdentityDenied = false;
try
{
    await using var targetScope = await TenantPostgreSqlTransaction.BeginAsync(runtimeFactory,
        new TenantDatabaseContext(provisioningSeed.Context.CompanyId, provisioningSeed.Context.BranchId, Guid.Empty,
            targetIssuer, targetSubject, "gl.accounts.manage"));
}
catch (UnauthorizedAccessException) { unlinkedTargetIdentityDenied = true; }
Assert(unlinkedTargetIdentityDenied,
    "an OIDC subject without authenticated enrollment proof cannot resolve to the target internal user");

Guid elevatedRequestId;
await using (var requesterScope = await TenantPostgreSqlTransaction.BeginAsync(runtimeFactory,
    new TenantDatabaseContext(provisioningSeed.Context.CompanyId, provisioningSeed.Context.BranchId, Guid.Empty,
        requesterIssuer, requesterSubject, "security.provisioning.request")))
await using (var request = new NpgsqlCommand("""
    SELECT monarch_security.request_provisioning_change(
        @company,@branch,'ASSIGN_ROLE',@target,NULL,NULL,@role,'Attempt unauthorized role escalation')
    """, requesterScope.Connection, requesterScope.Transaction))
{
    request.Parameters.AddWithValue("company", provisioningSeed.Context.CompanyId);
    request.Parameters.AddWithValue("branch", provisioningSeed.Context.BranchId);
    request.Parameters.AddWithValue("target", provisioningTarget.UserId);
    request.Parameters.AddWithValue("role", elevatedRoleId);
    elevatedRequestId = (Guid)(await request.ExecuteScalarAsync()
        ?? throw new InvalidOperationException("Elevated role request returned no ID."));
    await requesterScope.CommitAsync();
}
var escalationRejected = false;
try
{
    await using var approverScope = await TenantPostgreSqlTransaction.BeginAsync(runtimeFactory,
        new TenantDatabaseContext(provisioningSeed.Context.CompanyId, provisioningSeed.Context.BranchId, Guid.Empty,
            approverIssuer, provisioningApproverSubject, "security.provisioning.approve"));
    await using var approve = new NpgsqlCommand(
        "SELECT monarch_security.approve_provisioning_change(@request,'must fail privilege delegation')",
        approverScope.Connection, approverScope.Transaction);
    approve.Parameters.AddWithValue("request", elevatedRequestId);
    await approve.ExecuteNonQueryAsync();
    await approverScope.CommitAsync();
}
catch (PostgresException exception) when (exception.SqlState == "28000")
{
    escalationRejected = true;
}
Assert(escalationRejected, "approver cannot delegate a permission absent from their own effective permission set");

var selfTargetRejected = false;
try
{
    await using var requesterScope = await TenantPostgreSqlTransaction.BeginAsync(runtimeFactory,
        new TenantDatabaseContext(provisioningSeed.Context.CompanyId, provisioningSeed.Context.BranchId, Guid.Empty,
            requesterIssuer, requesterSubject, "security.provisioning.request"));
    await using var selfTarget = new NpgsqlCommand("""
        SELECT monarch_security.request_provisioning_change(
            @company,@branch,'ASSIGN_ROLE',@requester,NULL,NULL,@role,'Self grant attempt')
        """, requesterScope.Connection, requesterScope.Transaction);
    selfTarget.Parameters.AddWithValue("company", provisioningSeed.Context.CompanyId);
    selfTarget.Parameters.AddWithValue("branch", provisioningSeed.Context.BranchId);
    selfTarget.Parameters.AddWithValue("requester", provisioningRequester.UserId);
    selfTarget.Parameters.AddWithValue("role", targetRoleId);
    await selfTarget.ExecuteScalarAsync();
    await requesterScope.CommitAsync();
}
catch (PostgresException exception) when (exception.SqlState == "28000")
{
    selfTargetRejected = true;
}
Assert(selfTargetRejected, "provisioning requester cannot target their own user for role assignment");

var runtimeRbacWriteDenied = false;
try
{
    await using var runtimeConnection = await runtimeFactory.OpenConnectionAsync();
    await using var directMutation = new NpgsqlCommand(
        "UPDATE monarch_security.user_roles SET is_active=false WHERE company_id=@company",
        runtimeConnection);
    directMutation.Parameters.AddWithValue("company", provisioningSeed.Context.CompanyId);
    await directMutation.ExecuteNonQueryAsync();
}
catch (PostgresException exception) when (exception.SqlState == "42501")
{
    runtimeRbacWriteDenied = true;
}
Assert(runtimeRbacWriteDenied, "API runtime cannot directly mutate RBAC assignment tables");

var provisioningAuditCount = 0;
await using (var auditConnection = await adminFactory.OpenConnectionAsync())
await using (var audit = new NpgsqlCommand("""
    SELECT count(*) FROM monarch_security.authorization_audit
    WHERE entity_table='provisioning_requests'
      AND entity_key->>'request_id' IS NOT NULL
    """, auditConnection))
    provisioningAuditCount = Convert.ToInt32(await audit.ExecuteScalarAsync());
Assert(provisioningAuditCount >= 3, "provisioning request creation and decisions are captured in immutable authorization audit");

var cash = Guid.NewGuid();
var equity = Guid.NewGuid();
await gl.CreateAccountAsync(tenant, new GeneralLedgerAccount(cash, "1000", "Cash", "ASSET", "D"));
await gl.CreateAccountAsync(tenant, new GeneralLedgerAccount(equity, "3000", "Owner Equity", "EQUITY", "C"));

// Retrying identical account creation must not duplicate the transactional audit event.
await gl.CreateAccountAsync(tenant, new GeneralLedgerAccount(cash, "1000", "Cash", "ASSET", "D"));
await using (var accountAuditScope = await TenantPostgreSqlTransaction.BeginAsync(runtimeFactory, first.Context))
await using (var accountAudit = new NpgsqlCommand("""
    SELECT count(*) FROM monarch_outbox
    WHERE event_type='GeneralLedger.AccountCreated'
      AND (aggregate_id=@cash OR aggregate_id=@equity)
    """, accountAuditScope.Connection, accountAuditScope.Transaction))
{
    accountAudit.Parameters.AddWithValue("cash", cash.ToString("D"));
    accountAudit.Parameters.AddWithValue("equity", equity.ToString("D"));
    Assert(Convert.ToInt64(await accountAudit.ExecuteScalarAsync()) == 2,
        "GL account creation writes one transactional, idempotent audit/outbox event per account");
}

var concurrentBuilder = new NpgsqlConnectionStringBuilder(runtimeBuilder.ConnectionString)
{
    Pooling = true, MinPoolSize = 0, MaxPoolSize = 10
};
await using (var concurrentFactory = new PostgreSqlConnectionFactory(
    new PostgreSqlOptions(concurrentBuilder.ConnectionString, requireTls: false, contextHmacKey: contextKey)))
{
    var concurrentStore = new PostgreSqlGeneralLedgerStore(concurrentFactory);
    var concurrentDrafts = Enumerable.Range(0, 8).Select(_ => concurrentStore.CreateDraftAsync(
        tenant, Guid.NewGuid(), DateTime.UtcNow.Year, DateOnly.FromDateTime(DateTime.UtcNow),
        "Concurrent numbering test", "SAR", new[]
        {
            new GeneralLedgerLineInput(cash, 100, 0),
            new GeneralLedgerLineInput(equity, 0, 100)
        })).ToArray();
    var allocated = await Task.WhenAll(concurrentDrafts);
    Assert(allocated.Select(x => x.JournalNumber).Distinct().Count() == allocated.Length,
        "concurrent GL journal numbering is unique and serialized");
}

var journalId = Guid.NewGuid();
var today = DateOnly.FromDateTime(DateTime.UtcNow);
var journal = await gl.CreateDraftAsync(tenant, journalId, today.Year, today, "Opening cash balance", "SAR",
    new[]
    {
        new GeneralLedgerLineInput(cash, 12500, 0, "Cash debit"),
        new GeneralLedgerLineInput(equity, 0, 12500, "Equity credit")
    });
Assert(journal.Status == "DRAFT" && journal.JournalNumber > 0, "GL draft and atomic sequence allocation");
var peerUser = await AddUserToTenantAsync(adminFactory, first.Context);
var peerBranch = await AddBranchToTenantAsync(adminFactory, first.Context);
await gl.PostAsync(tenant, journalId);
await using (var peerScope = await TenantPostgreSqlTransaction.BeginAsync(runtimeFactory, peerUser))
{
    await using var peerVisibility = new NpgsqlCommand("""
        SELECT count(*) FILTER (WHERE status='POSTED'), count(*) FILTER (WHERE status='DRAFT')
        FROM monarch_gl_journals
        """, peerScope.Connection, peerScope.Transaction);
    await using var reader = await peerVisibility.ExecuteReaderAsync();
    await reader.ReadAsync();
    Assert(reader.GetInt64(0) == 1 && reader.GetInt64(1) == 0,
        "same-branch users see posted GL journals but not another user's drafts");
    await reader.DisposeAsync();
}
await using (var branchScope = await TenantPostgreSqlTransaction.BeginAsync(runtimeFactory, peerBranch))
await using (var branchVisibility = new NpgsqlCommand("SELECT count(*) FROM monarch_gl_journals", branchScope.Connection, branchScope.Transaction))
    Assert(Convert.ToInt64(await branchVisibility.ExecuteScalarAsync()) == 0, "GL journals are isolated by branch");
await using (var otherCompanyScope = await TenantPostgreSqlTransaction.BeginAsync(runtimeFactory, second.Context))
await using (var otherCompanyVisibility = new NpgsqlCommand("SELECT count(*) FROM monarch_gl_accounts", otherCompanyScope.Connection, otherCompanyScope.Transaction))
    Assert(Convert.ToInt64(await otherCompanyVisibility.ExecuteScalarAsync()) == 0, "GL chart of accounts is isolated by company");
await gl.PostAsync(tenant, journalId); // retry after lost response is idempotent
var trialBalance = await gl.GetTrialBalanceAsync(tenant, today, "SAR");
Assert(trialBalance.Sum(x => x.DebitMinor) == 12500 && trialBalance.Sum(x => x.CreditMinor) == 12500,
    "GL trial balance reconciles in integer minor units");
var visibleAccounts = await gl.GetAccountsAsync(tenant);
Assert(visibleAccounts.Count == 2 && visibleAccounts.Any(x => x.AccountId == cash) &&
    visibleAccounts.Any(x => x.AccountId == equity),
    "tenant-scoped GL account read returns the company's direct chart-of-accounts entries");
var journalSummaries = await gl.GetJournalsAsync(tenant);
Assert(journalSummaries.Count(x => x.JournalId == journalId) == 1 &&
    journalSummaries.Single(x => x.JournalId == journalId).TotalDebitMinor == 12500,
    "tenant-scoped journal list returns header and aggregated debit amount");
Assert((await gl.GetJournalsAsync(tenant, 1)).Count <= 1,
    "journal list enforces caller-selected bounded result size");
Assert((await gl.GetJournalsAsync(new TenantScope(peerBranch.CompanyId, peerBranch.BranchId, peerBranch.UserId))).Count == 0,
    "journal list is isolated from other branches");
var invalidJournalLimitRejected = false;
try { await gl.GetJournalsAsync(tenant, 0); }
catch (ArgumentOutOfRangeException) { invalidJournalLimitRejected = true; }
Assert(invalidJournalLimitRejected, "journal list rejects invalid result limits");
var journalDetail = await gl.GetJournalAsync(tenant, journalId);
Assert(journalDetail is not null && journalDetail.Journal.Status == "POSTED" &&
    journalDetail.CreatedBy == tenant.UserId && journalDetail.PostedBy == tenant.UserId &&
    journalDetail.Lines.Count == 2 && journalDetail.Lines.Sum(x => x.DebitMinor) == 12500 &&
    journalDetail.Lines.Sum(x => x.CreditMinor) == 12500,
    "tenant-scoped journal detail returns header, actor metadata and ordered lines");
Assert(await gl.GetJournalAsync(tenant, Guid.NewGuid()) is null,
    "missing or RLS-hidden journal detail returns no record");
var peerTenant = new TenantScope(peerUser.CompanyId, peerUser.BranchId, peerUser.UserId);
Assert(await gl.GetJournalAsync(peerTenant, journalId) is not null,
    "another authorized branch user can read a posted journal");


var invalidReversalRejected = false;
try
{
    await gl.CreateDraftAsync(tenant, Guid.NewGuid(), today.Year, today, "Incorrect reversal", "SAR",
        new[]
        {
            new GeneralLedgerLineInput(cash, 0, 12000),
            new GeneralLedgerLineInput(equity, 12000, 0)
        }, journalId);
}
catch (InvalidOperationException) { invalidReversalRejected = true; }
Assert(invalidReversalRejected, "GL reversal must exactly invert source account balances");

var reversalId = Guid.NewGuid();
await gl.CreateDraftAsync(tenant, reversalId, today.Year, today, "Exact reversal", "SAR",
    new[]
    {
        new GeneralLedgerLineInput(cash, 0, 12500),
        new GeneralLedgerLineInput(equity, 12500, 0)
    }, journalId);
await gl.PostAsync(tenant, reversalId);
var duplicateReversalRejected = false;
try
{
    await gl.CreateDraftAsync(tenant, Guid.NewGuid(), today.Year, today, "Duplicate reversal", "SAR",
        new[]
        {
            new GeneralLedgerLineInput(cash, 0, 12500),
            new GeneralLedgerLineInput(equity, 12500, 0)
        }, journalId);
}
catch (InvalidOperationException) { duplicateReversalRejected = true; }
Assert(duplicateReversalRejected, "only one reversal can reference a posted journal");
var reversedBalance = await gl.GetTrialBalanceAsync(tenant, today, "SAR");
Assert(reversedBalance.All(x => x.DebitMinor == x.CreditMinor),
    "posted exact reversal restores each account balance");

// Differential parity check against the retained SQLite ledger implementation using identical IDs and minor units.
var sqlitePath = Path.Combine(Path.GetTempPath(), "monarch-gl-parity-" + Guid.NewGuid().ToString("N") + ".db");
var sqliteStore = new SqliteLedgerStore(sqlitePath);
sqliteStore.Initialize(Path.Combine(Directory.GetCurrentDirectory(), "database", "ledger-storage.sql"));
sqliteStore.AddAccount(new Account(cash, "1000", "Cash", AccountType.Asset));
sqliteStore.AddAccount(new Account(equity, "3000", "Owner Equity", AccountType.Equity));
var sqliteEntry = sqliteStore.CreateDraft("SQLITE-PARITY-001", today, "Opening cash balance",
    new[] { JournalLine.Debit(cash, 12500), JournalLine.Credit(equity, 12500) });
sqliteStore.Post(sqliteEntry.Id);
sqliteStore.Reverse(sqliteEntry.Id, "SQLITE-PARITY-002", today, "Parity reversal");
var sqliteTrialBalance = sqliteStore.GetTrialBalance(today, today);
var sqliteByAccount = sqliteTrialBalance.Accounts.ToDictionary(x => x.AccountId);
Assert(reversedBalance.Count == sqliteByAccount.Count &&
    reversedBalance.All(row => sqliteByAccount.TryGetValue(row.AccountId, out var sqliteRow) &&
        row.DebitMinor == sqliteRow.DebitMinor && row.CreditMinor == sqliteRow.CreditMinor),
    "SQLite and PostgreSQL GL trial balances match for identical journals and reversals");
try { File.Delete(sqlitePath); } catch { }

// The database, not only the repository, rejects unbalanced journal posting.
var unbalancedId = Guid.NewGuid();
await gl.CreateDraftAsync(tenant, unbalancedId, today.Year, today, "Database balance guard", "SAR",
    new[]
    {
        new GeneralLedgerLineInput(cash, 1000, 0),
        new GeneralLedgerLineInput(equity, 0, 1000)
    });
await using (var peerScope = await TenantPostgreSqlTransaction.BeginAsync(runtimeFactory, peerUser))
await using (var peerDrafts = new NpgsqlCommand("SELECT count(*) FROM monarch_gl_journals WHERE status='DRAFT'", peerScope.Connection, peerScope.Transaction))
    Assert(Convert.ToInt64(await peerDrafts.ExecuteScalarAsync()) == 0, "another user cannot read a draft journal before posting");
await using (var scope = await TenantPostgreSqlTransaction.BeginAsync(runtimeFactory, first.Context))
{
    await using var corrupt = new NpgsqlCommand("""
        UPDATE monarch_gl_journal_lines SET credit_minor=999
        WHERE company_id=@c AND branch_id=@b AND journal_id=@j AND line_number=2
        """, scope.Connection, scope.Transaction);
    corrupt.Parameters.AddWithValue("c", first.Context.CompanyId);
    corrupt.Parameters.AddWithValue("b", first.Context.BranchId);
    corrupt.Parameters.AddWithValue("j", unbalancedId);
    await corrupt.ExecuteNonQueryAsync();
    await scope.CommitAsync();
}
var balanceGuard = false;
try { await gl.PostAsync(tenant, unbalancedId); }
catch (PostgresException) { balanceGuard = true; }
Assert(balanceGuard, "database trigger rejects unbalanced journal posting");

var immutable = false;
try
{
    await using var scope = await TenantPostgreSqlTransaction.BeginAsync(runtimeFactory, first.Context);
    await using var change = new NpgsqlCommand("""
        UPDATE monarch_gl_journal_lines SET debit_minor=12499
        WHERE company_id=@c AND branch_id=@b AND journal_id=@j AND line_number=1
        """, scope.Connection, scope.Transaction);
    change.Parameters.AddWithValue("c", first.Context.CompanyId);
    change.Parameters.AddWithValue("b", first.Context.BranchId);
    change.Parameters.AddWithValue("j", journalId);
    await change.ExecuteNonQueryAsync();
}
catch (PostgresException) { immutable = true; }
Assert(immutable, "posted GL journal lines are immutable");

Console.WriteLine("PASS: PostgreSQL 16 migrations, signed RLS, pooled context, membership lifecycle, GL and outbox integration.");

static async Task<TenantDatabaseContext> AddUserToTenantAsync(PostgreSqlConnectionFactory factory, TenantDatabaseContext existing)
{
    var user = Guid.NewGuid();
    await using var connection = await factory.OpenConnectionAsync();
    await using var transaction = await connection.BeginTransactionAsync();
    await ExecuteAsync(connection, transaction,
        "INSERT INTO monarch_tenant_users(company_id,user_id,normalized_login,display_name) VALUES (@c,@u,@login,@name)",
        ("c", existing.CompanyId), ("u", user), ("login", "peer-" + user.ToString("N")), ("name", "Peer User"));
    await ExecuteAsync(connection, transaction,
        "INSERT INTO monarch_user_branch_grants(company_id,branch_id,user_id) VALUES (@c,@b,@u)",
        ("c", existing.CompanyId), ("b", existing.BranchId), ("u", user));
    await transaction.CommitAsync();
    return new TenantDatabaseContext(existing.CompanyId, existing.BranchId, user);
}

static async Task<TenantDatabaseContext> AddBranchToTenantAsync(PostgreSqlConnectionFactory factory, TenantDatabaseContext existing)
{
    var branch = Guid.NewGuid();
    await using var connection = await factory.OpenConnectionAsync();
    await using var transaction = await connection.BeginTransactionAsync();
    await ExecuteAsync(connection, transaction,
        "INSERT INTO monarch_branches(company_id,branch_id,branch_code,branch_name) VALUES (@c,@b,@code,@name)",
        ("c", existing.CompanyId), ("b", branch), ("code", "OTHER-" + branch.ToString("N")), ("name", "Other Branch"));
    await ExecuteAsync(connection, transaction,
        "INSERT INTO monarch_user_branch_grants(company_id,branch_id,user_id) VALUES (@c,@b,@u)",
        ("c", existing.CompanyId), ("b", branch), ("u", existing.UserId));
    await transaction.CommitAsync();
    return new TenantDatabaseContext(existing.CompanyId, branch, existing.UserId);
}

static async Task SeedOidcAuthorizationAsync(
    PostgreSqlConnectionFactory factory, TenantDatabaseContext tenant, string issuer, string subject, params string[] permissions)
{
    if (permissions.Length == 0 || permissions.Any(string.IsNullOrWhiteSpace))
        throw new ArgumentException("At least one non-empty permission is required.", nameof(permissions));
    var role = Guid.NewGuid();
    await using var connection = await factory.OpenConnectionAsync();
    await using var transaction = await connection.BeginTransactionAsync();
    await ExecuteAsync(connection, transaction,
        "INSERT INTO monarch_security.external_identities(issuer,subject,company_id,user_id) VALUES (@issuer,@subject,@company,@user)",
        ("issuer", issuer), ("subject", subject), ("company", tenant.CompanyId), ("user", tenant.UserId));
    await ExecuteAsync(connection, transaction,
        "INSERT INTO monarch_security.roles(company_id,role_id,role_code,role_name) VALUES (@company,@role,@code,@name)",
        ("company", tenant.CompanyId), ("role", role), ("code", "CI-" + role.ToString("N")), ("name", "CI Integration Role"));
    foreach (var permission in permissions.Distinct(StringComparer.Ordinal))
        await ExecuteAsync(connection, transaction,
            "INSERT INTO monarch_security.role_permissions(company_id,role_id,permission_code) VALUES (@company,@role,@permission)",
            ("company", tenant.CompanyId), ("role", role), ("permission", permission));
    await ExecuteAsync(connection, transaction,
        "INSERT INTO monarch_security.user_roles(company_id,user_id,role_id,assigned_by) VALUES (@company,@user,@role,@actor)",
        ("company", tenant.CompanyId), ("user", tenant.UserId), ("role", role), ("actor", tenant.UserId));
    await transaction.CommitAsync();
}

static async Task<TenantSeed> SeedTenantAsync(PostgreSqlConnectionFactory factory, string suffix)
{
    var company = Guid.NewGuid();
    var branch = Guid.NewGuid();
    var user = Guid.NewGuid();
    await using var connection = await factory.OpenConnectionAsync();
    await using var transaction = await connection.BeginTransactionAsync();
    await ExecuteAsync(connection, transaction,
        "INSERT INTO monarch_companies(company_id,company_code,legal_name) VALUES (@c,@code,@name)",
        ("c", company), ("code", suffix + "-" + company.ToString("N")), ("name", "MONARCH " + suffix));
    await ExecuteAsync(connection, transaction,
        "INSERT INTO monarch_branches(company_id,branch_id,branch_code,branch_name) VALUES (@c,@b,@code,@name)",
        ("c", company), ("b", branch), ("code", "BR-" + branch.ToString("N")), ("name", suffix + " Branch"));
    await ExecuteAsync(connection, transaction,
        "INSERT INTO monarch_tenant_users(company_id,user_id,normalized_login,display_name) VALUES (@c,@u,@login,@name)",
        ("c", company), ("u", user), ("login", "ci-" + user.ToString("N")), ("name", suffix + " User"));
    await ExecuteAsync(connection, transaction,
        "INSERT INTO monarch_user_branch_grants(company_id,branch_id,user_id) VALUES (@c,@b,@u)",
        ("c", company), ("b", branch), ("u", user));
    await transaction.CommitAsync();
    return new TenantSeed(new TenantDatabaseContext(company, branch, user));
}

static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql,
    params (string Name, object Value)[] parameters)
{
    await using var command = new NpgsqlCommand(sql, connection, transaction);
    foreach (var (name, value) in parameters)
        command.Parameters.AddWithValue(name, value);
    await command.ExecuteNonQueryAsync();
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + message);
    Console.WriteLine("PASS: " + message);
}

static async Task ExpectUnauthorizedAsync(Func<Task> action, string message)
{
    var rejected = false;
    try { await action(); }
    catch (UnauthorizedAccessException) { rejected = true; }
    Assert(rejected, message);
}

sealed record TenantSeed(TenantDatabaseContext Context);

sealed class RecordingGeneralLedgerStore : IGeneralLedgerStore
{
    public TenantScope? LastTenant { get; private set; }
    public GeneralLedgerAccount? LastAccount { get; private set; }

    public Task CreateAccountAsync(TenantScope tenant, GeneralLedgerAccount account, CancellationToken cancellationToken = default)
    {
        LastTenant = tenant;
        LastAccount = account;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<GeneralLedgerAccount>> GetAccountsAsync(TenantScope tenant, bool includeInactive = false,
        CancellationToken cancellationToken = default)
    {
        LastTenant = tenant;
        return Task.FromResult<IReadOnlyList<GeneralLedgerAccount>>(Array.Empty<GeneralLedgerAccount>());
    }

    public Task<GeneralLedgerJournal> CreateDraftAsync(TenantScope tenant, Guid journalId, int fiscalYear,
        DateOnly journalDate, string description, string currencyCode, IReadOnlyCollection<GeneralLedgerLineInput> lines,
        Guid? reversesJournalId = null, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<IReadOnlyList<GeneralLedgerJournalSummary>> GetJournalsAsync(TenantScope tenant, int limit = 250,
        CancellationToken cancellationToken = default)
    {
        LastTenant = tenant;
        return Task.FromResult<IReadOnlyList<GeneralLedgerJournalSummary>>(Array.Empty<GeneralLedgerJournalSummary>());
    }

    public Task<GeneralLedgerJournalDetail?> GetJournalAsync(TenantScope tenant, Guid journalId,
        CancellationToken cancellationToken = default) => throw new NotImplementedException();

    public Task PostAsync(TenantScope tenant, Guid journalId, CancellationToken cancellationToken = default)
    {
        LastTenant = tenant;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<GeneralLedgerTrialBalanceRow>> GetTrialBalanceAsync(TenantScope tenant,
        DateOnly asOfDate, string currencyCode, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
}


sealed class FixedConfigurationManager<T>(T configuration) : IConfigurationManager<T> where T : class
{
    public Task<T> GetConfigurationAsync(CancellationToken cancellationToken) =>
        Task.FromResult(configuration);

    public void RequestRefresh() { }
}

sealed class FixedOptionsMonitor<T>(T currentValue) : IOptionsMonitor<T> where T : class
{
    public T CurrentValue => currentValue;
    public T Get(string? name) => currentValue;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
