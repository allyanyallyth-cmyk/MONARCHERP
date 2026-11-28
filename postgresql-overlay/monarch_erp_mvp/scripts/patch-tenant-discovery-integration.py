from pathlib import Path
import sys

root = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else Path(__file__).resolve().parents[1]
target = root / "tests" / "Monarch.PostgreSql.IntegrationTests" / "Program.cs"
if not target.is_file():
    raise SystemExit(f"Required PostgreSQL integration test source was not found: {target}")

source = target.read_text(encoding="utf-8")
mapping_anchor = "    endpointApp.MapGeneralLedgerEndpoints();\n"
mapping_patch = mapping_anchor + "    endpointApp.MapTenantDiscoveryEndpoints();\n"
route_anchor = '''    Assert(glRoutes.Length == 7 && glRoutes.All(route =>
        route.Metadata.GetMetadata<IAuthorizeData>() is not null),
        "all seven GL HTTP routes carry authorization metadata");
'''
route_patch = route_anchor + '''    var tenantDiscoveryRoutes = allRoutes.Where(route =>
        route.RoutePattern.RawText?.StartsWith("/api/v1/tenant-context/", StringComparison.Ordinal) == true).ToArray();
    Assert(tenantDiscoveryRoutes.Length == 2 && tenantDiscoveryRoutes.All(route =>
        route.Metadata.GetMetadata<IAuthorizeData>() is not null),
        "both OIDC tenant-discovery routes carry authorization metadata");
'''
seed_anchor = '''await SeedOidcAuthorizationAsync(adminFactory, federatedSeed.Context, testIssuer, testSubject,
    "gl.accounts.manage", "security.identity.invite", "security.identity.enrollment.approve");
'''
seed_patch = seed_anchor + '''// Tenant discovery is bound to the validated OIDC issuer/subject and exposes only active memberships.
var discoveryPrincipal = new ClaimsPrincipal(new ClaimsIdentity(new[]
{
    new Claim("iss", testIssuer), new Claim("sub", testSubject)
}, "validated-tenant-discovery-test"));
var discoveryService = new TenantDiscoveryApplicationService(runtimeFactory);
var discoveredCompanies = await discoveryService.ListCompaniesAsync(discoveryPrincipal);
Assert(discoveredCompanies.Count == 1 && discoveredCompanies[0].CompanyId == federatedSeed.Context.CompanyId &&
    discoveredCompanies[0].CompanyCode.StartsWith("FED-", StringComparison.Ordinal),
    "OIDC company discovery returns only companies linked to the active authenticated subject");
var discoveredBranches = await discoveryService.ListBranchesAsync(discoveryPrincipal, federatedSeed.Context.CompanyId);
Assert(discoveredBranches.Count == 1 && discoveredBranches[0].BranchId == federatedSeed.Context.BranchId,
    "OIDC branch discovery returns only active branch grants for the selected company");
var extraDiscoveryBranch = await AddBranchToTenantAsync(adminFactory, federatedSeed.Context);
var revokedDiscoveryBranch = await AddBranchToTenantAsync(adminFactory, federatedSeed.Context);
var ungrantedDiscoveryBranchId = Guid.NewGuid();
await using (var discoveryAdmin = await adminFactory.OpenConnectionAsync())
{
    await using var addUnassignedBranch = new NpgsqlCommand("""
        INSERT INTO monarch_branches(company_id,branch_id,branch_code,branch_name)
        VALUES(@company,@branch,@code,'Unassigned branch')
        """, discoveryAdmin);
    addUnassignedBranch.Parameters.AddWithValue("company", federatedSeed.Context.CompanyId);
    addUnassignedBranch.Parameters.AddWithValue("branch", ungrantedDiscoveryBranchId);
    addUnassignedBranch.Parameters.AddWithValue("code", "NO-GRANT-" + ungrantedDiscoveryBranchId.ToString("N"));
    await addUnassignedBranch.ExecuteNonQueryAsync();
    await using var revokeDiscoveryGrant = new NpgsqlCommand("""
        UPDATE monarch_user_branch_grants SET is_active=false, revoked_at=clock_timestamp()
        WHERE company_id=@company AND branch_id=@branch AND user_id=@user
        """, discoveryAdmin);
    revokeDiscoveryGrant.Parameters.AddWithValue("company", federatedSeed.Context.CompanyId);
    revokeDiscoveryGrant.Parameters.AddWithValue("branch", revokedDiscoveryBranch.BranchId);
    revokeDiscoveryGrant.Parameters.AddWithValue("user", federatedSeed.Context.UserId);
    Assert(await revokeDiscoveryGrant.ExecuteNonQueryAsync() == 1,
        "integration fixture revokes one branch grant before discovery verification");
}
discoveredBranches = await discoveryService.ListBranchesAsync(discoveryPrincipal, federatedSeed.Context.CompanyId);
var discoveredBranchIds = discoveredBranches.Select(branch => branch.BranchId).ToHashSet();
Assert(discoveredBranchIds.SetEquals(new[] { federatedSeed.Context.BranchId, extraDiscoveryBranch.BranchId }) &&
    !discoveredBranchIds.Contains(revokedDiscoveryBranch.BranchId) &&
    !discoveredBranchIds.Contains(ungrantedDiscoveryBranchId),
    "OIDC branch discovery excludes revoked grants and branches without membership");
var wrongSubjectPrincipal = new ClaimsPrincipal(new ClaimsIdentity(new[]
{
    new Claim("iss", testIssuer), new Claim("sub", "unknown-subject")
}, "validated-tenant-discovery-test"));
Assert((await discoveryService.ListCompaniesAsync(wrongSubjectPrincipal)).Count == 0,
    "OIDC company discovery returns no data for an unmapped subject");
Assert((await discoveryService.ListBranchesAsync(discoveryPrincipal, first.Context.CompanyId)).Count == 0,
    "OIDC branch discovery cannot enumerate branches from another company");
await ExpectUnauthorizedAsync(
    () => discoveryService.ListCompaniesAsync(new ClaimsPrincipal(new ClaimsIdentity())),
    "OIDC tenant discovery rejects anonymous principals");
'''

patches = [
    (mapping_anchor, mapping_patch, "route mapping"),
    (route_anchor, route_patch, "route authorization assertions"),
    (seed_anchor, seed_patch, "PostgreSQL tenant-discovery data integration assertions"),
]
for anchor, replacement, label in patches:
    if replacement in source:
        continue
    if source.count(anchor) != 1:
        raise SystemExit(f"Expected exactly one {label} anchor, found {source.count(anchor)}; refusing an unsafe patch.")
    source = source.replace(anchor, replacement, 1)

if "OIDC branch discovery excludes revoked grants and branches without membership" not in source:
    raise SystemExit("Tenant discovery integration patch is incomplete.")
target.write_text(source, encoding="utf-8", newline="\n")
print("PASS: PostgreSQL integration test now exercises OIDC company/branch discovery, revoked/ungranted branches, wrong subjects and cross-company probes.")
