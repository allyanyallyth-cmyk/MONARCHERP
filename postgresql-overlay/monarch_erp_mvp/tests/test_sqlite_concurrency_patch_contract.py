from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
PATCH = ROOT / "scripts" / "patch-sqlite-concurrency-test.py"
PIPELINE = ROOT / "scripts" / "patch-windows-source-strings.ps1"
STRESS_TEST = ROOT / "tests" / "test_sqlite_multitenant_concurrency.py"


def test_sqlite_contention_test_keeps_300_writes_with_bounded_concurrency():
    source = STRESS_TEST.read_text(encoding="utf-8")
    assert "ThreadPoolExecutor(max_workers=16)" in source
    assert "executor.map(writer, range(300))" in source
    assert "assert sum(r[2] for r in rows) == 300" in source
    assert "assert all(r[2] == 25 for r in rows)" in source
    assert "threads = [threading.Thread(target=writer" not in source


def test_ci_pipeline_applies_an_idempotent_fail_closed_stress_patch():
    patch = PATCH.read_text(encoding="utf-8")
    pipeline = PIPELINE.read_text(encoding="utf-8")
    assert "source.count(old_workers) != 1" in patch
    assert "unknown partial worker-pool patch" in patch
    assert "patch-sqlite-concurrency-test.py" in pipeline
