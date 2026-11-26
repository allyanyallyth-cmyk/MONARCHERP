from __future__ import annotations

import re
import sys
from pathlib import Path


def replace_once(path: Path, old: str, new: str) -> None:
    text = path.read_text(encoding="utf-8")
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{path}: expected exactly one occurrence, found {count}: {old[:100]!r}")
    path.write_text(text.replace(old, new), encoding="utf-8", newline="\n")


def patch(root: Path) -> None:
    store = root / "src/Monarch.Core/Accounting/SqliteLedgerStore.cs"
    replace_once(
        store,
        "new SqliteConnectionStringBuilder { DataSource = fullPath, ForeignKeys = true }.ToString();",
        "new SqliteConnectionStringBuilder { DataSource = fullPath, ForeignKeys = true, Pooling = false }.ToString();",
    )

    atomicity = root / "tests/test_backup_restore_failure_atomicity.py"
    source = atomicity.read_text(encoding="utf-8")
    if "from contextlib import closing" not in source:
        source = source.replace("import sqlite3\n", "import sqlite3\nfrom contextlib import closing\n", 1)
    source, n = re.subn(
        r"with sqlite3\.connect\(([^\n]+?)\) as db:",
        r"with closing(sqlite3.connect(\1)) as db:",
        source,
    )
    if n < 3:
        raise RuntimeError(f"{atomicity}: expected to close candidate/live/test connections; changed {n}")
    replace_once(
        atomicity,
        '        db.execute("INSERT INTO sentinel VALUES(?)", (value,))\n',
        '        db.execute("INSERT INTO sentinel VALUES(?)", (value,))\n        db.commit()\n',
    )
    atomicity.write_text(source, encoding="utf-8", newline="\n")

    journal = root / "tests/test_backup_restore_crash_recovery_journal.py"
    source = journal.read_text(encoding="utf-8")
    old = 'journal.read_bytes().startswith(b"MONARCH-RESTORE-JOURNAL-V2\\n")'
    new = 'journal.read_text(encoding="utf-8").startswith("MONARCH-RESTORE-JOURNAL-V2\\n")'
    if source.count(old) != 2:
        raise RuntimeError(f"{journal}: expected two newline-sensitive assertions")
    journal.write_text(source.replace(old, new), encoding="utf-8", newline="\n")

    for relative in (
        "tests/test_commercial_master_data_queries.py",
        "tests/test_inventory_valuation_store.py",
    ):
        path = root / relative
        source = path.read_text(encoding="utf-8")
        source, n = re.subn(
            r"(?m)^(\s*)with tempfile\.NamedTemporaryFile\(\) as f:\s*$",
            lambda m: (
                m.group(1) + "with tempfile.TemporaryDirectory() as temp_dir:\n"
                + m.group(1) + "    db_path = Path(temp_dir) / \"test.db\""
            ),
            source,
        )
        if n == 0:
            raise RuntimeError(f"{path}: no NamedTemporaryFile blocks found")
        source = source.replace("sqlite3.connect(f.name)", "sqlite3.connect(db_path)")
        if "f.name" in source:
            raise RuntimeError(f"{path}: unexpected remaining f.name reference")
        path.write_text(source, encoding="utf-8", newline="\n")

    stock = root / "tests/test_stock_movement_sequence.py"
    stock.write_text("""from contextlib import closing
from pathlib import Path
import sqlite3, tempfile, unittest

ROOT = Path(__file__).resolve().parents[1]
SCHEMA = (ROOT / 'database/commercial-data.sql').read_text()
STORE = (ROOT / 'src/Monarch.Core/Commercial/CommercialStore.cs').read_text()
VALUATION = (ROOT / 'src/Monarch.Core/Commercial/InventoryValuationStore.cs').read_text()
ENGINE = (ROOT / 'src/Monarch.Core/Commercial/InventoryValuationEngine.cs').read_text()


class StockMovementSequenceTests(unittest.TestCase):
    def test_new_schema_has_monotonic_sequence_column(self):
        self.assertIn('ledger_sequence INTEGER NOT NULL DEFAULT 0 CHECK(ledger_sequence>=0)', SCHEMA)

    def test_record_and_transfer_assign_sequence_inside_transaction(self):
        self.assertIn('NextMovementSequence(c, tx, companyId, m.WarehouseId, m.ItemId)', STORE)
        self.assertIn('NextMovementSequence(c, tx, companyId, warehouseId, itemId)', STORE)
        self.assertIn('MAX(ledger_sequence),0)+1', STORE)

    def test_legacy_database_upgrade_is_idempotent_and_indexed(self):
        self.assertIn('EnsureMovementSequence(c)', STORE)
        self.assertIn('PRAGMA table_info(monarch_stock_movements)', STORE)
        self.assertIn('ux_stock_movement_ledger_sequence', STORE)

    def test_valuation_uses_persisted_sequence_not_clock(self):
        self.assertIn('ORDER BY ledger_sequence', VALUATION)
        self.assertIn('reader.GetInt64(2)', VALUATION)
        self.assertIn('OrderBy(x => x.Sequence)', ENGINE)

    def test_backfill_preserves_legacy_order_for_equal_timestamps(self):
        with tempfile.TemporaryDirectory() as directory:
            with closing(sqlite3.connect(Path(directory) / 'test.db')) as c:
                c.execute('CREATE TABLE m(id TEXT, company_id TEXT, warehouse_id TEXT, item_id TEXT, occurred_utc TEXT, ledger_sequence INTEGER DEFAULT 0)')
                c.executemany('INSERT INTO m VALUES(?,?,?,?,?,0)', [('b','c','w','i','2026-01-01'),('a','c','w','i','2026-01-01'),('c','c','w','i','2026-01-02')])
                sql = """WITH ranked AS (SELECT rowid rid, ROW_NUMBER() OVER(PARTITION BY company_id,warehouse_id,item_id ORDER BY occurred_utc,rowid) seq FROM m WHERE ledger_sequence=0) UPDATE m SET ledger_sequence=(SELECT seq FROM ranked WHERE ranked.rid=m.rowid) WHERE rowid IN(SELECT rid FROM ranked);"""
                c.executescript(sql)
                self.assertEqual([('b',1),('a',2),('c',3)], c.execute('SELECT id,ledger_sequence FROM m ORDER BY ledger_sequence').fetchall())

    def test_migration_temporarily_bypasses_and_restores_immutable_guard(self):
        self.assertIn('DROP TRIGGER IF EXISTS trg_stock_movement_no_update', STORE)
        self.assertIn('CREATE TRIGGER IF NOT EXISTS trg_stock_movement_no_update', STORE)
        with tempfile.TemporaryDirectory() as directory:
            with closing(sqlite3.connect(Path(directory) / 'test.db')) as c:
                c.executescript("""CREATE TABLE movements(id INTEGER PRIMARY KEY, seq INTEGER); INSERT INTO movements(seq) VALUES(0); CREATE TRIGGER no_update BEFORE UPDATE ON movements BEGIN SELECT RAISE(ABORT, 'immutable'); END;""")
                c.execute('DROP TRIGGER no_update')
                c.execute('UPDATE movements SET seq=1 WHERE id=1')
                c.execute("CREATE TRIGGER no_update BEFORE UPDATE ON movements BEGIN SELECT RAISE(ABORT, 'immutable'); END;")
                with self.assertRaises(sqlite3.IntegrityError):
                    c.execute('UPDATE movements SET seq=2 WHERE id=1')

    def test_sequence_is_scoped_per_warehouse_and_item(self):
        with tempfile.TemporaryDirectory() as directory:
            with closing(sqlite3.connect(Path(directory) / 'test.db')) as c:
                c.execute('CREATE TABLE m(company_id,warehouse_id,item_id,ledger_sequence)')
                c.executemany('INSERT INTO m VALUES(?,?,?,?)',[('c','w1','i',1),('c','w2','i',1),('c','w1','j',1)])
                self.assertEqual(2,c.execute("SELECT COALESCE(MAX(ledger_sequence),0)+1 FROM m WHERE company_id='c' AND warehouse_id='w1' AND item_id='i'").fetchone()[0])


if __name__ == '__main__':
    unittest.main()
""", encoding="utf-8", newline="\n")

    tenant = root / "tests/test_tenant_accounting_schema.py"
    replace_once(
        tenant,
        "UPDATE monarch_tenant_journal_lines SET journal_id='j1' WHERE journal_id='j2' LIMIT 1",
        "UPDATE monarch_tenant_journal_lines SET journal_id='j1' WHERE rowid IN (SELECT rowid FROM monarch_tenant_journal_lines WHERE journal_id='j2' LIMIT 1)",
    )


if __name__ == "__main__":
    if len(sys.argv) != 2:
        raise SystemExit("usage: apply_monarch_windows_repairs.py <extracted-monarch_erp_mvp-root>")
    root = Path(sys.argv[1]).resolve()
    if not (root / "MonarchERP.sln").is_file():
        raise SystemExit(f"MONARCH source root not found: {root}")
    patch(root)
    print("Applied deterministic Windows restore and SQLite test compatibility repairs.")
