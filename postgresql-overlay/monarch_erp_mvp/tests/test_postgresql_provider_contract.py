from pathlib import Path
import unittest
ROOT = Path(__file__).resolve().parents[1]
class PostgreSqlProviderContractTests(unittest.TestCase):
    def test_provider_uses_net8_and_npgsql(self):
        p=(ROOT/"src/Monarch.PostgreSql/Monarch.PostgreSql.csproj").read_text()
        self.assertIn("<TargetFramework>net8.0</TargetFramework>",p)
        self.assertIn('PackageReference Include="Npgsql"',p)
    def test_tenant_scope_requires_identity_and_membership(self):
        p=(ROOT/"src/Monarch.PostgreSql/Infrastructure/TenantDatabaseContext.cs").read_text()
        for field in ("CompanyId","BranchId","UserId"): self.assertIn(field,p)
        self.assertIn("UnauthorizedAccessException",p)
        self.assertIn("monarch_user_branch_grants",p)
    def test_all_tenant_tables_force_rls(self):
        p=(ROOT/"database/postgresql/001_central_tenancy.sql").read_text()
        for table in ("monarch_companies","monarch_branches","monarch_tenant_users","monarch_user_branch_grants","monarch_outbox"):
            self.assertIn(f"ALTER TABLE {table} ENABLE ROW LEVEL SECURITY",p)
            self.assertIn(f"ALTER TABLE {table} FORCE ROW LEVEL SECURITY",p)
    def test_outbox_is_idempotent(self):
        p=(ROOT/"src/Monarch.PostgreSql/Infrastructure/PostgreSqlOutboxWriter.cs").read_text()
        self.assertIn("ON CONFLICT (company_id, event_id) DO NOTHING",p)
if __name__=="__main__": unittest.main()