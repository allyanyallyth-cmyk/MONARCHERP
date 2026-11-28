using Microsoft.Data.Sqlite;

namespace Monarch.Core.Commercial;

/// <summary>Company/branch-isolated commercial master data and append-only stock ledger.</summary>
public sealed class CommercialStore
{
    private readonly string _connectionString;
    public CommercialStore(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath)) throw new ArgumentException("Database path is required.", nameof(databasePath));
        var full = Path.GetFullPath(databasePath); Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = full, ForeignKeys = true }.ToString();
    }
    public void Initialize(string? schemaPath = null)
    {
        schemaPath ??= Path.Combine(AppContext.BaseDirectory, "database", "commercial-data.sql");
        if (!File.Exists(schemaPath)) throw new FileNotFoundException("Commercial schema not found.", schemaPath);
        using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = File.ReadAllText(schemaPath); cmd.ExecuteNonQuery();
        EnsureMovementSequence(c);
    }
    /// <summary>Creates a company-scoped customer/supplier party with granular RBAC and atomic audit.</summary>
    public void AddParty(Guid userId, Guid companyId, Party p)
    {
        if (p is null) throw new ArgumentNullException(nameof(p));
        if (!Enum.IsDefined(typeof(PartyType), p.Type)) throw new ArgumentOutOfRangeException(nameof(p), "Unknown party type.");
        if (p.CreditLimitMinor < 0 || p.PaymentTermsDays < 0) throw new ArgumentOutOfRangeException(nameof(p));
        var code = Required(p.Code, nameof(p.Code));
        var legalName = Required(p.LegalName, nameof(p.LegalName));
        var displayName = Required(p.DisplayName, nameof(p.DisplayName));
        using var c = Open(); using var tx = c.BeginTransaction();
        if (p.Type == PartyType.Customer || p.Type == PartyType.Both)
            RequireCompanyPermission(c, tx, userId, companyId, "commercial.sales.manage");
        if (p.Type == PartyType.Supplier || p.Type == PartyType.Both)
            RequireCompanyPermission(c, tx, userId, companyId, "commercial.purchases.manage");
        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = @"INSERT INTO monarch_parties(id,company_id,code,party_type,legal_name,display_name,tax_number,phone,email,address,credit_limit_minor,payment_terms_days,is_active,created_utc)
VALUES($id,$company,$code,$type,$legal,$display,$tax,$phone,$email,$address,$credit,$terms,$active,$now);";
            cmd.Parameters.AddWithValue("$id", p.Id.ToString()); cmd.Parameters.AddWithValue("$company", companyId.ToString());
            cmd.Parameters.AddWithValue("$code", code); cmd.Parameters.AddWithValue("$type", p.Type.ToString());
            cmd.Parameters.AddWithValue("$legal", legalName); cmd.Parameters.AddWithValue("$display", displayName);
            AddNullable(cmd,"$tax",p.TaxNumber); AddNullable(cmd,"$phone",p.Phone); AddNullable(cmd,"$email",p.Email); AddNullable(cmd,"$address",p.Address);
            cmd.Parameters.AddWithValue("$credit",p.CreditLimitMinor); cmd.Parameters.AddWithValue("$terms",p.PaymentTermsDays);
            cmd.Parameters.AddWithValue("$active",p.IsActive?1:0); cmd.Parameters.AddWithValue("$now",DateTimeOffset.UtcNow.ToString("O")); cmd.ExecuteNonQuery();
        }
        var action = p.Type == PartyType.Customer ? "CustomerPartyCreated" :
            p.Type == PartyType.Supplier ? "SupplierPartyCreated" : "CommercialPartyCreated";
        InsertMasterDataAudit(c, tx, userId, companyId, null, action, "CommercialParty", p.Id,
            "type=" + p.Type + ";code=" + code);
        tx.Commit();
    }
    /// <summary>Reads company-scoped commercial parties with type-specific RBAC in the same transaction.</summary>
    public IReadOnlyList<Party> GetParties(Guid userId, Guid companyId, PartyType? type = null, bool activeOnly = true)
    {
        if (type.HasValue && !Enum.IsDefined(typeof(PartyType), type.Value))
            throw new ArgumentOutOfRangeException(nameof(type), "Unknown party type.");

        using var c = Open();
        using var tx = c.BeginTransaction();
        if (type == PartyType.Customer)
            RequireCompanyPermission(c, tx, userId, companyId, "commercial.sales.manage");
        else if (type == PartyType.Supplier)
            RequireCompanyPermission(c, tx, userId, companyId, "commercial.purchases.manage");
        else
        {
            // Unfiltered and Both-party queries can disclose both customer and supplier records.
            RequireCompanyPermission(c, tx, userId, companyId, "commercial.sales.manage");
            RequireCompanyPermission(c, tx, userId, companyId, "commercial.purchases.manage");
        }

        var result = new List<Party>();
        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = @"SELECT id,code,party_type,legal_name,display_name,tax_number,phone,email,address,
credit_limit_minor,payment_terms_days,is_active FROM monarch_parties
WHERE company_id=$co AND ($type IS NULL OR party_type=$type OR ($type='Customer' AND party_type='Both') OR ($type='Supplier' AND party_type='Both'))
AND ($active=0 OR is_active=1) ORDER BY code COLLATE NOCASE;";
            cmd.Parameters.AddWithValue("$co", companyId.ToString());
            cmd.Parameters.AddWithValue("$type", type.HasValue ? type.Value.ToString() : DBNull.Value);
            cmd.Parameters.AddWithValue("$active", activeOnly ? 1 : 0);
            using var r = cmd.ExecuteReader();
            while (r.Read()) result.Add(new Party(Guid.Parse(r.GetString(0)), r.GetString(1),
                Enum.Parse<PartyType>(r.GetString(2), false), r.GetString(3), r.GetString(4),
                r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6),
                r.IsDBNull(7) ? null : r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8),
                r.GetInt64(9), r.GetInt32(10), r.GetInt64(11) == 1));
        }
        tx.Commit();
        return result;
    }

    public void AddUnit(Guid userId, Guid companyId, UnitOfMeasure unit)
    {
        if (unit.DecimalPlaces is < 0 or > 6) throw new ArgumentOutOfRangeException(nameof(unit));
        using var c = Open(); using var tx = c.BeginTransaction();
        RequireCompanyPermission(c, tx, userId, companyId, "inventory.items.manage");
        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO monarch_units(id,company_id,code,name,decimal_places) VALUES($id,$co,$code,$name,$dp);";
            cmd.Parameters.AddWithValue("$id", unit.Id.ToString()); cmd.Parameters.AddWithValue("$co", companyId.ToString());
            cmd.Parameters.AddWithValue("$code", Required(unit.Code, nameof(unit.Code))); cmd.Parameters.AddWithValue("$name", Required(unit.Name, nameof(unit.Name)));
            cmd.Parameters.AddWithValue("$dp", unit.DecimalPlaces); cmd.ExecuteNonQuery();
        }
        InsertMasterDataAudit(c, tx, userId, companyId, null, "UnitCreated", "UnitOfMeasure", unit.Id, "code=" + unit.Code.Trim());
        tx.Commit();
    }
    public void AddItem(Guid userId, Guid companyId, StockItem item)
    {
        if (item.CostMinor < 0 || item.SalePriceMinor < 0 || item.ReorderLevelMilli < 0) throw new ArgumentOutOfRangeException(nameof(item));
        using var c = Open(); using var tx = c.BeginTransaction();
        RequireCompanyPermission(c, tx, userId, companyId, "inventory.items.manage");
        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = @"INSERT INTO monarch_items(id,company_id,group_id,unit_id,sku,barcode,name,item_type,cost_minor,sale_price_minor,reorder_level_milli,allow_negative,is_active,created_utc)
VALUES($id,$co,$group,$unit,$sku,$barcode,$name,$type,$cost,$price,$reorder,$negative,$active,$now);";
            cmd.Parameters.AddWithValue("$id", item.Id.ToString()); cmd.Parameters.AddWithValue("$co", companyId.ToString()); AddNullable(cmd, "$group", item.GroupId?.ToString());
            cmd.Parameters.AddWithValue("$unit", item.UnitId.ToString()); cmd.Parameters.AddWithValue("$sku", Required(item.Sku, nameof(item.Sku))); AddNullable(cmd, "$barcode", item.Barcode);
            cmd.Parameters.AddWithValue("$name", Required(item.Name, nameof(item.Name))); cmd.Parameters.AddWithValue("$type", item.Type.ToString()); cmd.Parameters.AddWithValue("$cost", item.CostMinor);
            cmd.Parameters.AddWithValue("$price", item.SalePriceMinor); cmd.Parameters.AddWithValue("$reorder", item.ReorderLevelMilli); cmd.Parameters.AddWithValue("$negative", item.AllowNegative ? 1 : 0);
            cmd.Parameters.AddWithValue("$active", item.IsActive ? 1 : 0); cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O")); cmd.ExecuteNonQuery();
        }
        InsertMasterDataAudit(c, tx, userId, companyId, null, "InventoryItemCreated", "InventoryItem", item.Id, "sku=" + item.Sku.Trim());
        tx.Commit();
    }
    public void AddWarehouse(Guid userId, Guid companyId, Warehouse warehouse)
    {
        using var c = Open(); using var tx = c.BeginTransaction();
        RequireBranchPermission(c, tx, userId, companyId, warehouse.BranchId, "inventory.warehouses.manage");
        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO monarch_warehouses(id,company_id,branch_id,code,name,is_active,created_utc) VALUES($id,$co,$branch,$code,$name,$active,$now);";
            cmd.Parameters.AddWithValue("$id", warehouse.Id.ToString()); cmd.Parameters.AddWithValue("$co", companyId.ToString()); cmd.Parameters.AddWithValue("$branch", warehouse.BranchId.ToString());
            cmd.Parameters.AddWithValue("$code", Required(warehouse.Code, nameof(warehouse.Code))); cmd.Parameters.AddWithValue("$name", Required(warehouse.Name, nameof(warehouse.Name)));
            cmd.Parameters.AddWithValue("$active", warehouse.IsActive ? 1 : 0); cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O")); cmd.ExecuteNonQuery();
        }
        InsertMasterDataAudit(c, tx, userId, companyId, warehouse.BranchId, "WarehouseCreated", "Warehouse", warehouse.Id, "code=" + warehouse.Code.Trim());
        tx.Commit();
    }
    /// <summary>Lists company-scoped item master data. Read-only; never initializes or migrates schema.</summary>
    public IReadOnlyList<StockItem> GetItems(Guid userId, Guid companyId, bool activeOnly = true)
    {
        var result = new List<StockItem>();
        using var c = Open(); using var tx = c.BeginTransaction();
        RequireCompanyRead(c, tx, userId, companyId);
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = @"SELECT id,unit_id,sku,name,item_type,cost_minor,sale_price_minor,reorder_level_milli,group_id,barcode,allow_negative,is_active
FROM monarch_items WHERE company_id=$co AND ($active=0 OR is_active=1) ORDER BY sku COLLATE NOCASE;";
        cmd.Parameters.AddWithValue("$co", companyId.ToString()); cmd.Parameters.AddWithValue("$active", activeOnly ? 1 : 0);
        using (var r = cmd.ExecuteReader())
            while (r.Read()) result.Add(new StockItem(Guid.Parse(r.GetString(0)), Guid.Parse(r.GetString(1)),
                r.GetString(2), r.GetString(3), Enum.Parse<ItemType>(r.GetString(4), false), r.GetInt64(5),
                r.GetInt64(6), r.GetInt64(7), r.IsDBNull(8) ? null : Guid.Parse(r.GetString(8)),
                r.IsDBNull(9) ? null : r.GetString(9), r.GetInt64(10) == 1, r.GetInt64(11) == 1));
        tx.Commit(); return result;
    }

    /// <summary>Lists warehouses only within the selected company and authorized branch.</summary>
    public IReadOnlyList<Warehouse> GetWarehouses(Guid userId, Guid companyId, Guid branchId, bool activeOnly = true)
    {
        var result = new List<Warehouse>();
        using var c = Open(); using var tx = c.BeginTransaction();
        RequireBranch(c, tx, userId, companyId, branchId, false);
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = @"SELECT id,branch_id,code,name,is_active FROM monarch_warehouses
WHERE company_id=$co AND branch_id=$br AND ($active=0 OR is_active=1) ORDER BY code COLLATE NOCASE;";
        cmd.Parameters.AddWithValue("$co", companyId.ToString()); cmd.Parameters.AddWithValue("$br", branchId.ToString());
        cmd.Parameters.AddWithValue("$active", activeOnly ? 1 : 0);
        using (var r = cmd.ExecuteReader())
            while (r.Read()) result.Add(new Warehouse(Guid.Parse(r.GetString(0)), Guid.Parse(r.GetString(1)),
                r.GetString(2), r.GetString(3), r.GetInt64(4) == 1));
        tx.Commit(); return result;
    }

    public void RecordMovement(Guid userId, Guid companyId, Guid branchId, StockMovement m)
    {
        if(m.QuantityMilli<=0||m.UnitCostMinor<0) throw new ArgumentOutOfRangeException(nameof(m));
        if(string.IsNullOrWhiteSpace(m.IdempotencyKey)) throw new ArgumentException("An idempotency key is required.",nameof(m));
        using var c=Open(); using var tx=c.BeginTransaction();
        // Authorization and mutation share one transaction to prevent membership or
        // permission changes between the access check and the stock-ledger write.
        RequireBranch(c, tx, userId, companyId, branchId, true);
        using(var check=c.CreateCommand()) { check.Transaction=tx; check.CommandText="SELECT 1 FROM monarch_warehouses WHERE company_id=$co AND id=$wh AND branch_id=$br AND is_active=1;";
            check.Parameters.AddWithValue("$co",companyId.ToString()); check.Parameters.AddWithValue("$wh",m.WarehouseId.ToString()); check.Parameters.AddWithValue("$br",branchId.ToString());
            if(check.ExecuteScalar()==null) throw new UnauthorizedAccessException("Warehouse is not active in the selected branch."); }
        using(var check=c.CreateCommand()) { check.Transaction=tx; check.CommandText="SELECT allow_negative,is_active,item_type FROM monarch_items WHERE company_id=$co AND id=$item;";
            check.Parameters.AddWithValue("$co",companyId.ToString()); check.Parameters.AddWithValue("$item",m.ItemId.ToString()); using var r=check.ExecuteReader();
            if(!r.Read()||r.GetInt64(1)!=1) throw new InvalidOperationException("Item does not exist or is inactive in this company.");
            if(r.GetString(2)!="Stock") throw new InvalidOperationException("Only stock items can have inventory movements."); }
        bool outbound=m.Type is StockMovementType.Issue or StockMovementType.TransferOut or StockMovementType.AdjustmentOut or StockMovementType.PurchaseReturn;
        if(outbound) {
            using var q=c.CreateCommand(); q.Transaction=tx; q.CommandText=@"SELECT COALESCE((SELECT quantity_milli FROM monarch_stock_balances WHERE company_id=$co AND warehouse_id=$wh AND item_id=$item),0),allow_negative FROM monarch_items WHERE company_id=$co AND id=$item;";
            q.Parameters.AddWithValue("$co",companyId.ToString()); q.Parameters.AddWithValue("$wh",m.WarehouseId.ToString()); q.Parameters.AddWithValue("$item",m.ItemId.ToString()); using var r=q.ExecuteReader(); r.Read();
            long current=r.GetInt64(0); bool allow=r.GetInt64(1)==1; if(!allow && current<m.QuantityMilli) throw new InvalidOperationException("Insufficient stock; negative inventory is disabled.");
        }
        long ledgerSequence = NextMovementSequence(c, tx, companyId, m.WarehouseId, m.ItemId);
        using(var cmd=c.CreateCommand()) { cmd.Transaction=tx; cmd.CommandText=@"INSERT INTO monarch_stock_movements(id,company_id,branch_id,warehouse_id,item_id,ledger_sequence,movement_type,quantity_milli,unit_cost_minor,source_type,source_id,idempotency_key,occurred_utc,actor_user_id)
VALUES($id,$co,$br,$wh,$item,$sequence,$type,$qty,$cost,$sourceType,$sourceId,$key,$now,$actor);";
            cmd.Parameters.AddWithValue("$id",m.Id.ToString()); cmd.Parameters.AddWithValue("$co",companyId.ToString()); cmd.Parameters.AddWithValue("$br",branchId.ToString()); cmd.Parameters.AddWithValue("$wh",m.WarehouseId.ToString());
            cmd.Parameters.AddWithValue("$item",m.ItemId.ToString()); cmd.Parameters.AddWithValue("$sequence",ledgerSequence); cmd.Parameters.AddWithValue("$type",m.Type.ToString()); cmd.Parameters.AddWithValue("$qty",m.QuantityMilli); cmd.Parameters.AddWithValue("$cost",m.UnitCostMinor);
            AddNullable(cmd,"$sourceType",m.SourceType); AddNullable(cmd,"$sourceId",m.SourceId); cmd.Parameters.AddWithValue("$key",m.IdempotencyKey);
            cmd.Parameters.AddWithValue("$now",DateTimeOffset.UtcNow.ToString("O")); cmd.Parameters.AddWithValue("$actor",userId.ToString()); cmd.ExecuteNonQuery(); }
        InsertInventoryAudit(c, tx, userId, companyId, branchId,
            "StockMovement" + m.Type, m.Id, m.WarehouseId, m.ItemId,
            m.QuantityMilli, m.UnitCostMinor, m.SourceType, m.SourceId, m.IdempotencyKey);
        tx.Commit();
    }
    /// <summary>Moves stock between two active warehouses as one database transaction.</summary>
    public void TransferStock(Guid userId, Guid companyId, Guid sourceBranchId, Guid sourceWarehouseId,
        Guid targetBranchId, Guid targetWarehouseId, Guid itemId, long quantityMilli,
        string idempotencyKey)
    {
        if (sourceWarehouseId == targetWarehouseId) throw new ArgumentException("Source and destination warehouses must differ.");
        if (quantityMilli <= 0) throw new ArgumentOutOfRangeException(nameof(quantityMilli));
        if (string.IsNullOrWhiteSpace(idempotencyKey)) throw new ArgumentException("An idempotency key is required.", nameof(idempotencyKey));

        using var c = Open();
        using var tx = c.BeginTransaction();
        // Authorize both branches inside the exact transaction that writes both transfer legs.
        RequireBranch(c, tx, userId, companyId, sourceBranchId, true);
        RequireBranch(c, tx, userId, companyId, targetBranchId, true);
        ValidateWarehouse(c, tx, companyId, sourceBranchId, sourceWarehouseId);
        ValidateWarehouse(c, tx, companyId, targetBranchId, targetWarehouseId);
        bool allowNegative;
        using (var item = c.CreateCommand())
        {
            item.Transaction = tx;
            item.CommandText = "SELECT allow_negative FROM monarch_items WHERE company_id=$co AND id=$item AND is_active=1 AND item_type='Stock';";
            item.Parameters.AddWithValue("$co", companyId.ToString()); item.Parameters.AddWithValue("$item", itemId.ToString());
            var value = item.ExecuteScalar();
            if (value == null) throw new InvalidOperationException("Active stock item not found in this company.");
            allowNegative = Convert.ToInt64(value) == 1;
        }
        long sourceQuantity;
        using (var balance = c.CreateCommand())
        {
            balance.Transaction = tx;
            balance.CommandText = "SELECT COALESCE((SELECT quantity_milli FROM monarch_stock_balances WHERE company_id=$co AND warehouse_id=$wh AND item_id=$item),0);";
            balance.Parameters.AddWithValue("$co", companyId.ToString()); balance.Parameters.AddWithValue("$wh", sourceWarehouseId.ToString()); balance.Parameters.AddWithValue("$item", itemId.ToString());
            sourceQuantity = Convert.ToInt64(balance.ExecuteScalar());
        }
        if (!allowNegative && sourceQuantity < quantityMilli) throw new InvalidOperationException("Insufficient stock in source warehouse.");
        if (sourceQuantity < quantityMilli) throw new InvalidOperationException("A transfer cannot derive a carrying cost from a negative or empty source balance.");

        // Transfer cost is derived from the source warehouse's persisted movement ledger;
        // callers cannot inject an arbitrary unit cost. This release uses moving weighted average.
        long transferUnitCost = CalculateCurrentMovingAverageCost(c, tx, companyId, sourceBranchId,
            sourceWarehouseId, itemId);
        var outboundMovementId = InsertTransferLeg(c, tx, userId, companyId, sourceBranchId, sourceWarehouseId, itemId,
            StockMovementType.TransferOut, quantityMilli, transferUnitCost, idempotencyKey + ":OUT");
        var inboundMovementId = InsertTransferLeg(c, tx, userId, companyId, targetBranchId, targetWarehouseId, itemId,
            StockMovementType.TransferIn, quantityMilli, transferUnitCost, idempotencyKey + ":IN");
        var transferId = idempotencyKey;
        InsertInventoryAudit(c, tx, userId, companyId, sourceBranchId, "StockTransferOut",
            outboundMovementId, sourceWarehouseId, itemId, quantityMilli,
            transferUnitCost, "WarehouseTransfer", transferId, idempotencyKey + ":OUT");
        InsertInventoryAudit(c, tx, userId, companyId, targetBranchId, "StockTransferIn",
            inboundMovementId, targetWarehouseId, itemId, quantityMilli,
            transferUnitCost, "WarehouseTransfer", transferId, idempotencyKey + ":IN");
        tx.Commit();
    }

    private static long CalculateCurrentMovingAverageCost(SqliteConnection c, SqliteTransaction tx,
        Guid companyId, Guid branchId, Guid warehouseId, Guid itemId)
    {
        var movements = new List<InventoryValuationMovement>();
        using (var read = c.CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText = @"SELECT id,occurred_utc,ledger_sequence,movement_type,quantity_milli,unit_cost_minor
FROM monarch_stock_movements WHERE company_id=$co AND branch_id=$br AND warehouse_id=$wh AND item_id=$item
ORDER BY ledger_sequence;";
            read.Parameters.AddWithValue("$co", companyId.ToString()); read.Parameters.AddWithValue("$br", branchId.ToString());
            read.Parameters.AddWithValue("$wh", warehouseId.ToString()); read.Parameters.AddWithValue("$item", itemId.ToString());
            using var r = read.ExecuteReader();
            while (r.Read()) movements.Add(new InventoryValuationMovement(Guid.Parse(r.GetString(0)),
                DateTimeOffset.Parse(r.GetString(1)), r.GetInt64(2), Enum.Parse<StockMovementType>(r.GetString(3), false),
                r.GetInt64(4), r.GetInt64(5)));
        }
        var valuation = InventoryValuationEngine.Calculate(movements, InventoryCostMethod.MovingWeightedAverage);
        if (valuation.ClosingQuantityMilli <= 0) throw new InvalidOperationException("Source warehouse has no positive valued stock.");
        return valuation.AverageUnitCostMinor;
    }

    // Company-level master-data reads still require an active identity, membership, and inventory.read.
    private static void RequireCompanyRead(SqliteConnection c, SqliteTransaction tx, Guid user, Guid company)
    {
        using var q = c.CreateCommand(); q.Transaction = tx;
        q.CommandText = @"SELECT 1 FROM monarch_users u
JOIN monarch_user_company_access ca ON ca.user_id=u.id AND ca.company_id=$co AND ca.is_active=1
JOIN monarch_companies co ON co.id=ca.company_id AND co.is_active=1
WHERE u.id=$u AND u.is_active=1 AND EXISTS (SELECT 1 FROM monarch_user_roles ur
JOIN monarch_role_permissions rp ON rp.role_id=ur.role_id
WHERE ur.user_id=u.id AND rp.permission_code='inventory.read');";
        q.Parameters.AddWithValue("$u", user.ToString()); q.Parameters.AddWithValue("$co", company.ToString());
        if (q.ExecuteScalar() is null) throw new UnauthorizedAccessException("Active company access and inventory.read permission are required.");
    }

    // Authorization is evaluated in the same transaction as the paired transfer mutation.
    private static void RequireBranch(SqliteConnection c, SqliteTransaction tx, Guid user, Guid co, Guid br, bool write)
    {
        using var q = c.CreateCommand(); q.Transaction = tx;
        q.CommandText = @"SELECT 1
FROM monarch_users u
JOIN monarch_user_company_access ca ON ca.user_id=u.id AND ca.company_id=$co
JOIN monarch_user_branch_access ba ON ba.user_id=ca.user_id AND ba.company_id=ca.company_id AND ba.branch_id=$br
JOIN monarch_companies co ON co.id=ca.company_id
JOIN monarch_branches b ON b.company_id=ba.company_id AND b.id=ba.branch_id
WHERE u.id=$u AND u.is_active=1 AND ca.is_active=1 AND ba.is_active=1
AND co.is_active=1 AND b.is_active=1
AND EXISTS (SELECT 1 FROM monarch_user_roles ur
  JOIN monarch_role_permissions rp ON rp.role_id=ur.role_id
  WHERE ur.user_id=u.id AND rp.permission_code=$permission);";
        q.Parameters.AddWithValue("$u", user.ToString()); q.Parameters.AddWithValue("$co", co.ToString());
        q.Parameters.AddWithValue("$br", br.ToString());
        q.Parameters.AddWithValue("$permission", write ? "inventory.movements.manage" : "inventory.read");
        if (q.ExecuteScalar() is null)
            throw new UnauthorizedAccessException("Active company/branch access and the required inventory permission are required.");
    }

    private static long NextMovementSequence(SqliteConnection c, SqliteTransaction tx, Guid companyId, Guid warehouseId, Guid itemId)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "SELECT COALESCE(MAX(ledger_sequence),0)+1 FROM monarch_stock_movements WHERE company_id=$co AND warehouse_id=$wh AND item_id=$item;";
        cmd.Parameters.AddWithValue("$co", companyId.ToString()); cmd.Parameters.AddWithValue("$wh", warehouseId.ToString());
        cmd.Parameters.AddWithValue("$item", itemId.ToString());
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    // Idempotent upgrade for databases created before ledger_sequence existed.
    private static void EnsureMovementSequence(SqliteConnection c)
    {
        using var tx = c.BeginTransaction();
        bool exists;
        using (var check = c.CreateCommand())
        {
            check.Transaction = tx; check.CommandText = "PRAGMA table_info(monarch_stock_movements);";
            using var reader = check.ExecuteReader(); exists = false;
            while (reader.Read()) if (reader.GetString(1) == "ledger_sequence") exists = true;
        }
        if (!exists)
        {
            using var alter = c.CreateCommand(); alter.Transaction = tx;
            alter.CommandText = "ALTER TABLE monarch_stock_movements ADD COLUMN ledger_sequence INTEGER NOT NULL DEFAULT 0 CHECK(ledger_sequence>=0);";
            alter.ExecuteNonQuery();
        }
        // Temporarily remove the append-only guard only inside this schema migration.
        using (var dropGuard = c.CreateCommand())
        {
            dropGuard.Transaction = tx; dropGuard.CommandText = "DROP TRIGGER IF EXISTS trg_stock_movement_no_update;";
            dropGuard.ExecuteNonQuery();
        }
        using (var backfill = c.CreateCommand())
        {
            backfill.Transaction = tx;
            backfill.CommandText = @"WITH ranked AS (
 SELECT rowid AS rid,
 (SELECT COALESCE(MAX(existing.ledger_sequence),0) FROM monarch_stock_movements existing
  WHERE existing.company_id=current.company_id AND existing.warehouse_id=current.warehouse_id
   AND existing.item_id=current.item_id AND existing.ledger_sequence>0)
 + ROW_NUMBER() OVER(PARTITION BY company_id,warehouse_id,item_id ORDER BY occurred_utc,rowid) AS seq
 FROM monarch_stock_movements current WHERE ledger_sequence=0
) UPDATE monarch_stock_movements SET ledger_sequence=(SELECT seq FROM ranked WHERE ranked.rid=monarch_stock_movements.rowid)
WHERE rowid IN (SELECT rid FROM ranked);";
            backfill.ExecuteNonQuery();
        }
        using (var index = c.CreateCommand())
        {
            index.Transaction = tx;
            index.CommandText = @"CREATE UNIQUE INDEX IF NOT EXISTS ux_stock_movement_ledger_sequence
ON monarch_stock_movements(company_id,warehouse_id,item_id,ledger_sequence) WHERE ledger_sequence>0;
CREATE TRIGGER IF NOT EXISTS trg_stock_movement_no_update BEFORE UPDATE ON monarch_stock_movements
BEGIN SELECT RAISE(ABORT,'Stock movements are immutable'); END;";
            index.ExecuteNonQuery();
        }
        tx.Commit();
    }

    private static void InsertInventoryAudit(SqliteConnection c, SqliteTransaction tx, Guid userId,
        Guid companyId, Guid branchId, string action, Guid movementId, Guid warehouseId, Guid itemId,
        long quantityMilli, long unitCostMinor, string? sourceType, string? sourceId, string idempotencyKey)
    {
        using var audit = c.CreateCommand(); audit.Transaction = tx;
        audit.CommandText = @"INSERT INTO monarch_company_audit_log
(company_id,branch_id,actor_user_id,action,entity_type,entity_id,details,occurred_utc)
VALUES($co,$br,$actor,$action,'StockMovement',$id,$details,$now);";
        audit.Parameters.AddWithValue("$co", companyId.ToString());
        audit.Parameters.AddWithValue("$br", branchId.ToString());
        audit.Parameters.AddWithValue("$actor", userId.ToString());
        audit.Parameters.AddWithValue("$action", action);
        audit.Parameters.AddWithValue("$id", movementId.ToString());
        var details = "warehouse=" + warehouseId.ToString("D") + ";item=" + itemId.ToString("D") +
            ";quantityMilli=" + quantityMilli.ToString(System.Globalization.CultureInfo.InvariantCulture) +
            ";unitCostMinor=" + unitCostMinor.ToString(System.Globalization.CultureInfo.InvariantCulture) +
            ";sourceType=" + (sourceType ?? string.Empty) + ";sourceId=" + (sourceId ?? string.Empty) +
            ";idempotencyKey=" + idempotencyKey;
        audit.Parameters.AddWithValue("$details", details);
        audit.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        audit.ExecuteNonQuery();
    }

    private static void ValidateWarehouse(SqliteConnection c, SqliteTransaction tx, Guid companyId, Guid branchId, Guid warehouseId)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "SELECT 1 FROM monarch_warehouses WHERE company_id=$co AND id=$wh AND branch_id=$br AND is_active=1;";
        cmd.Parameters.AddWithValue("$co", companyId.ToString()); cmd.Parameters.AddWithValue("$wh", warehouseId.ToString()); cmd.Parameters.AddWithValue("$br", branchId.ToString());
        if (cmd.ExecuteScalar() == null) throw new UnauthorizedAccessException("Warehouse is not active in the selected branch.");
    }

    private static Guid InsertTransferLeg(SqliteConnection c, SqliteTransaction tx, Guid userId, Guid companyId,
        Guid branchId, Guid warehouseId, Guid itemId, StockMovementType type, long quantityMilli,
        long unitCostMinor, string key)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        var movementId = Guid.NewGuid();
        long ledgerSequence = NextMovementSequence(c, tx, companyId, warehouseId, itemId);
        cmd.CommandText = @"INSERT INTO monarch_stock_movements(id,company_id,branch_id,warehouse_id,item_id,ledger_sequence,movement_type,quantity_milli,unit_cost_minor,source_type,source_id,idempotency_key,occurred_utc,actor_user_id)
VALUES($id,$co,$br,$wh,$item,$sequence,$type,$qty,$cost,'WarehouseTransfer',$source,$key,$now,$actor);";
        cmd.Parameters.AddWithValue("$id", movementId.ToString()); cmd.Parameters.AddWithValue("$co", companyId.ToString());
        cmd.Parameters.AddWithValue("$br", branchId.ToString()); cmd.Parameters.AddWithValue("$wh", warehouseId.ToString());
        cmd.Parameters.AddWithValue("$item", itemId.ToString()); cmd.Parameters.AddWithValue("$sequence", ledgerSequence); cmd.Parameters.AddWithValue("$type", type.ToString());
        cmd.Parameters.AddWithValue("$qty", quantityMilli); cmd.Parameters.AddWithValue("$cost", unitCostMinor);
        cmd.Parameters.AddWithValue("$source", key.Substring(0, key.LastIndexOf(':'))); cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O")); cmd.Parameters.AddWithValue("$actor", userId.ToString());
        cmd.ExecuteNonQuery();
        return movementId;
    }

    public IReadOnlyList<StockBalance> GetBalances(Guid userId, Guid companyId, Guid branchId)
    {
        // Keep authorization and the branch-scoped read in one transaction so a revoked
        // membership or deactivated company/branch cannot be bypassed by the legacy helper.
        using var c = Open();
        using var tx = c.BeginTransaction();
        RequireBranch(c, tx, userId, companyId, branchId, write: false);
        var result = new List<StockBalance>();
        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = @"SELECT b.warehouse_id,b.item_id,b.quantity_milli
FROM monarch_stock_balances b
JOIN monarch_warehouses w ON w.company_id=b.company_id AND w.id=b.warehouse_id
WHERE b.company_id=$co AND w.branch_id=$br
ORDER BY b.warehouse_id,b.item_id;";
            cmd.Parameters.AddWithValue("$co", companyId.ToString());
            cmd.Parameters.AddWithValue("$br", branchId.ToString());
            using var r = cmd.ExecuteReader();
            while (r.Read()) result.Add(new StockBalance(Guid.Parse(r.GetString(0)),
                Guid.Parse(r.GetString(1)), r.GetInt64(2)));
        }
        tx.Commit();
        return result;
    }
    private static void RequireCompanyPermission(SqliteConnection c, SqliteTransaction tx, Guid user, Guid company, string permission)
    {
        using var q = c.CreateCommand(); q.Transaction = tx;
        q.CommandText = @"SELECT 1 FROM monarch_users u
JOIN monarch_user_company_access ca ON ca.user_id=u.id AND ca.company_id=$co AND ca.is_active=1
JOIN monarch_companies co ON co.id=ca.company_id AND co.is_active=1
WHERE u.id=$user AND u.is_active=1 AND EXISTS(SELECT 1 FROM monarch_user_roles ur
JOIN monarch_role_permissions rp ON rp.role_id=ur.role_id WHERE ur.user_id=u.id AND rp.permission_code=$permission);";
        q.Parameters.AddWithValue("$user", user.ToString()); q.Parameters.AddWithValue("$co", company.ToString()); q.Parameters.AddWithValue("$permission", permission);
        if (q.ExecuteScalar() is null) throw new UnauthorizedAccessException("Active company membership and the required inventory permission are required.");
    }
    private static void RequireBranchPermission(SqliteConnection c, SqliteTransaction tx, Guid user, Guid company, Guid branch, string permission)
    {
        using var q = c.CreateCommand(); q.Transaction = tx;
        q.CommandText = @"SELECT 1 FROM monarch_users u
JOIN monarch_user_company_access ca ON ca.user_id=u.id AND ca.company_id=$co AND ca.is_active=1
JOIN monarch_user_branch_access ba ON ba.user_id=u.id AND ba.company_id=ca.company_id AND ba.branch_id=$branch AND ba.is_active=1
JOIN monarch_companies co ON co.id=ca.company_id AND co.is_active=1
JOIN monarch_branches b ON b.company_id=co.id AND b.id=ba.branch_id AND b.is_active=1
WHERE u.id=$user AND u.is_active=1 AND EXISTS(SELECT 1 FROM monarch_user_roles ur
JOIN monarch_role_permissions rp ON rp.role_id=ur.role_id WHERE ur.user_id=u.id AND rp.permission_code=$permission);";
        q.Parameters.AddWithValue("$user", user.ToString()); q.Parameters.AddWithValue("$co", company.ToString());
        q.Parameters.AddWithValue("$branch", branch.ToString()); q.Parameters.AddWithValue("$permission", permission);
        if (q.ExecuteScalar() is null) throw new UnauthorizedAccessException("Active company/branch membership and the required inventory permission are required.");
    }
    private static void InsertMasterDataAudit(SqliteConnection c, SqliteTransaction tx, Guid user, Guid company, Guid? branch,
        string action, string entity, Guid id, string? details)
    {
        using var a = c.CreateCommand(); a.Transaction = tx;
        a.CommandText = @"INSERT INTO monarch_company_audit_log(company_id,branch_id,actor_user_id,action,entity_type,entity_id,details,occurred_utc)
VALUES($co,$branch,$user,$action,$entity,$id,$details,$now);";
        a.Parameters.AddWithValue("$co", company.ToString()); AddNullable(a, "$branch", branch?.ToString());
        a.Parameters.AddWithValue("$user", user.ToString()); a.Parameters.AddWithValue("$action", action);
        a.Parameters.AddWithValue("$entity", entity); a.Parameters.AddWithValue("$id", id.ToString()); AddNullable(a, "$details", details);
        a.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O")); a.ExecuteNonQuery();
    }

    private void RequireCompany(Guid user,Guid company,bool write) { using var c=Open(); using var cmd=c.CreateCommand(); cmd.CommandText="SELECT role_code FROM monarch_user_company_access WHERE user_id=$u AND company_id=$co AND is_active=1;"; cmd.Parameters.AddWithValue("$u",user.ToString()); cmd.Parameters.AddWithValue("$co",company.ToString()); var role=cmd.ExecuteScalar() as string; if(role==null) throw new UnauthorizedAccessException("No active company access."); if(write&&role=="Viewer") throw new UnauthorizedAccessException("Read-only role."); }
    private void RequireBranch(Guid user,Guid company,Guid branch,bool write) { RequireCompany(user,company,write); using var c=Open(); using var cmd=c.CreateCommand(); cmd.CommandText="SELECT 1 FROM monarch_user_branch_access WHERE user_id=$u AND company_id=$co AND branch_id=$br AND is_active=1;"; cmd.Parameters.AddWithValue("$u",user.ToString()); cmd.Parameters.AddWithValue("$co",company.ToString()); cmd.Parameters.AddWithValue("$br",branch.ToString()); if(cmd.ExecuteScalar()==null) throw new UnauthorizedAccessException("No active access to selected branch."); }
    private SqliteConnection Open() { var c=new SqliteConnection(_connectionString); c.Open(); using var p=c.CreateCommand(); p.CommandText="PRAGMA foreign_keys=ON;"; p.ExecuteNonQuery(); return c; }
    private static string Required(string? s,string name)=>string.IsNullOrWhiteSpace(s)?throw new ArgumentException("Value is required.",name):s.Trim();
    private static void AddNullable(SqliteCommand c,string name,string? value)=>c.Parameters.AddWithValue(name,(object?)value??DBNull.Value);
}
