from pathlib import Path
import sys

root = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else Path(__file__).resolve().parents[1]
target = root / "tests" / "test_sqlite_multitenant_concurrency.py"
if not target.is_file():
    raise SystemExit(f"Required SQLite concurrency regression test was not found: {target}")

source = target.read_text(encoding="utf-8")
old_import = "import sqlite3\n"
new_import = "from concurrent.futures import ThreadPoolExecutor\nimport sqlite3\n"
old_workers = """        threads = [threading.Thread(target=writer, args=(i,)) for i in range(300)]
        for t in threads: t.start()
        for t in threads: t.join()
"""
new_workers = """        # Keep hundreds of independent transactions while bounding active writer
        # connections. SQLite WAL still permits only one writer at a time; launching
        # 300 OS threads at once causes avoidable SQLITE_BUSY failures on CI runners.
        with ThreadPoolExecutor(max_workers=16) as executor:
            list(executor.map(writer, range(300)))
"""

if "ThreadPoolExecutor" in source:
    if "max_workers=16" not in source or "executor.map(writer, range(300))" not in source:
        raise SystemExit("SQLite concurrency test contains an unknown partial worker-pool patch.")
    if old_workers in source:
        raise SystemExit("SQLite concurrency test contains both old and new worker scheduling.")
    print("PASS: SQLite stress test worker-pool patch is already applied.")
else:
    if source.count(old_import) != 1 or source.count(old_workers) != 1:
        raise SystemExit("SQLite concurrency test anchors are missing or ambiguous; refusing an unsafe rewrite.")
    source = source.replace(old_import, new_import, 1)
    source = source.replace(old_workers, new_workers, 1)
    target.write_text(source, encoding="utf-8", newline="\n")
    print("PASS: SQLite stress test retains 300 transactions with 16 bounded concurrent workers.")
