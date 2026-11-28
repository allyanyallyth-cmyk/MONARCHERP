from pathlib import Path
import unittest
ROOT = Path(__file__).resolve().parents[1]
class WindowsCiPackagingTests(unittest.TestCase):
    def test_windows_ci_runs_python_dotnet_and_package_steps(self):
        workflow = (ROOT / '.github/workflows/windows-build.yml').read_text(encoding='utf-8')
        for token in ('windows-2022', 'dotnet restore MonarchERP.sln', 'dotnet build MonarchERP.sln',
                      'python -m pytest -q', 'PYTHONUTF8', 'Monarch.Core.SmokeTests',
                      'scripts/package-release.ps1', 'upload-artifact@v4'):
            self.assertIn(token, workflow)
    def test_release_script_requires_both_published_executables_and_hashes_zip(self):
        script = (ROOT / 'scripts/package-release.ps1').read_text(encoding='utf-8')
        self.assertIn('Monarch.Desktop.exe', script)
        self.assertIn('Monarch.Recovery.exe', script)
        self.assertIn('Get-FileHash', script)
        self.assertIn('SHA256', script)
    def test_windows_workflow_is_not_excluded_by_gitignore(self):
        ignored = (ROOT / '.gitignore').read_text(encoding='utf-8')
        workflow_path = '.github/workflows/windows-build.yml'
        self.assertNotIn(workflow_path, ignored.splitlines())
        self.assertTrue((ROOT / workflow_path).is_file())
    def test_ci_verifies_portable_and_setup_hashes_and_archive_contents(self):
        workflow = (ROOT / '.github/workflows/windows-build.yml').read_text(encoding='utf-8')
        verifier = (ROOT / 'scripts/verify-windows-release.ps1').read_text(encoding='utf-8')
        self.assertIn('verify-windows-release.ps1', workflow)
        for token in ('Get-FileHash', 'SHA256', 'Monarch.Desktop.exe', 'Monarch.Recovery.exe'):
            self.assertIn(token, verifier)
    def test_build_script_runs_both_test_suites_before_publish(self):
        script = (ROOT / 'scripts/build-windows.ps1').read_text(encoding='utf-8')
        self.assertLess(script.index('python -m unittest'), script.index('dotnet publish'))
        self.assertIn('Monarch.Core.SmokeTests', script)
        self.assertIn('dotnet build', script)
if __name__ == '__main__':
    unittest.main()
