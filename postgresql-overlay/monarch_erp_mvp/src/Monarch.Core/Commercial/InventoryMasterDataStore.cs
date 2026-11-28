using Microsoft.Data.Sqlite;

namespace Monarch.Core.Commercial;

/// <summary>Permission-checked, tenant-isolated CRUD for inventory items and warehouses.</summary>
public sealed class InventoryMasterDataStore
{
    private readonly string _connectionString;
    public InventoryMasterDataStore(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath)) throw new ArgumentException("Database path is required.", nameof(databasePath));
        var full = Path.GetFullPath(databasePath); Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = full, ForeignKeys = true }.ToString();
    }

    public IReadOnlyList<UnitOfMeasure> GetUnits(Guid userId, Guid companyId)
    {
        using var c = Open(); using var tx = c.BeginTransaction();
        Require(c, tx, userId, companyId, null, "inventory.read");
        var result = new List<UnitOfMeasure>();
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "SELECT id,code,name,decimal_places FROM monarch_units WHERE company_id=$co ORDER BY code COLLATE NOCASE;";
        cmd.Parameters.AddWithValue("$co", companyId.ToString());
        using (var r = cmd.ExecuteReader()) while (r.Read()) result.Add(new UnitOfMeasure(Guid.Parse(r.GetString(0)), r.GetString(1), r.GetString(2), r.GetInt32(3)));
        tx.Commit(); return result;
    }

    public IReadOnlyList<ItemGroup> GetItemGroups(Guid userId, Guid companyId)
    {
        using var c = Open(); using var tx = c.BeginTransaction();
        Require(c, tx, userId, companyId, null, "inventory.read");
        var result = new List<ItemGroup>();
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "SELECT id,parent_id,code,name,is_active FROM monarch_item_groups WHERE company_id=$co ORDER BY code COLLATE NOCASE;";
        cmd.Parameters.AddWithValue("$co", companyId.ToString());
        using (var r = cmd.ExecuteReader()) while (r.Read()) result.Add(new ItemGroup(Guid.Parse(r.GetString(0)), r.IsDBNull(1) ? null : Guid.Parse(r.GetString(1)), r.GetString(2), r.GetString(3), r.GetInt32(4) == 1));
        tx.Commit(); return result;
    }

    public Guid CreateItemGroup(Guid userId, Guid companyId, ItemGroup group)
    {
        ValidateGroup(group); var id = group.Id == Guid.Empty ? Guid.NewGuid() : group.Id;
        using var c = Open(); using var tx = c.BeginTransaction(); Require(c, tx, userId, companyId, null, "inventory.items.manage");
        ValidateGroupParent(c, tx, companyId, group.ParentId, null);
        using (var cmd = c.CreateCommand()) { cmd.Transaction = tx; cmd.CommandText = "INSERT INTO monarch_item_groups(id,company_id,parent_id,code,name,is_active) VALUES($id,$co,$parent,$code,$name,1);";
            cmd.Parameters.AddWithValue("$id", id.ToString()); cmd.Parameters.AddWithValue("$co", companyId.ToString()); Add(cmd, "$parent", group.ParentId?.ToString());
            cmd.Parameters.AddWithValue("$code", group.Code.Trim()); cmd.Parameters.AddWithValue("$name", group.Name.Trim()); cmd.ExecuteNonQuery(); }
        Audit(c, tx, userId, companyId, null, "ItemGroupCreated", "ItemGroup", id, "code=" + group.Code.Trim()); tx.Commit(); return id;
    }

    public void UpdateItemGroup(Guid userId, Guid companyId, ItemGroup group)
    {
        if (group.Id == Guid.Empty) throw new ArgumentException("معرف المجموعة مطلوب."); ValidateGroup(group);
        using var c = Open(); using var tx = c.BeginTransaction(); Require(c, tx, userId, companyId, null, "inventory.items.manage");
        ValidateGroupParent(c, tx, companyId, group.ParentId, group.Id);
        using (var cmd = c.CreateCommand()) { cmd.Transaction = tx; cmd.CommandText = "UPDATE monarch_item_groups SET parent_id=$parent,code=$code,name=$name WHERE company_id=$co AND id=$id AND is_active=1;";
            Add(cmd, "$parent", group.ParentId?.ToString()); cmd.Parameters.AddWithValue("$code", group.Code.Trim()); cmd.Parameters.AddWithValue("$name", group.Name.Trim());
            cmd.Parameters.AddWithValue("$co", companyId.ToString()); cmd.Parameters.AddWithValue("$id", group.Id.ToString());
            if (cmd.ExecuteNonQuery() != 1) throw new InvalidOperationException("المجموعة غير موجودة أو غير نشطة ضمن المنشأة."); }
        Audit(c, tx, userId, companyId, null, "ItemGroupUpdated", "ItemGroup", group.Id, "code=" + group.Code.Trim()); tx.Commit();
    }

    public void SetItemGroupActive(Guid userId, Guid companyId, Guid groupId, bool isActive)
    {
        using var c = Open(); using var tx = c.BeginTransaction(); Require(c, tx, userId, companyId, null, "inventory.items.manage");
        if (!isActive) { using var check = c.CreateCommand(); check.Transaction = tx; check.CommandText = @"SELECT
(SELECT COUNT(*) FROM monarch_items WHERE company_id=$co AND group_id=$id AND is_active=1) +
(SELECT COUNT(*) FROM monarch_item_groups WHERE company_id=$co AND parent_id=$id AND is_active=1);";
            check.Parameters.AddWithValue("$co", companyId.ToString()); check.Parameters.AddWithValue("$id", groupId.ToString());
            if (Convert.ToInt64(check.ExecuteScalar()) > 0) throw new InvalidOperationException("لا يمكن تعطيل مجموعة مرتبطة بأصناف نشطة أو مجموعات فرعية نشطة."); }
        using (var cmd = c.CreateCommand()) { cmd.Transaction = tx; cmd.CommandText = "UPDATE monarch_item_groups SET is_active=$active WHERE company_id=$co AND id=$id;";
            cmd.Parameters.AddWithValue("$active", isActive ? 1 : 0); cmd.Parameters.AddWithValue("$co", companyId.ToString()); cmd.Parameters.AddWithValue("$id", groupId.ToString());
            if (cmd.ExecuteNonQuery() != 1) throw new InvalidOperationException("مجموعة الأصناف غير موجودة."); }
        Audit(c, tx, userId, companyId, null, isActive ? "ItemGroupActivated" : "ItemGroupDeactivated", "ItemGroup", groupId, null); tx.Commit();
    }

    public Guid CreateUnit(Guid userId, Guid companyId, UnitOfMeasure unit)
    {
        ValidateUnit(unit); var id = unit.Id == Guid.Empty ? Guid.NewGuid() : unit.Id;
        using var c = Open(); using var tx = c.BeginTransaction(); Require(c, tx, userId, companyId, null, "inventory.items.manage");
        using (var cmd = c.CreateCommand()) { cmd.Transaction = tx; cmd.CommandText = "INSERT INTO monarch_units(id,company_id,code,name,decimal_places) VALUES($id,$co,$code,$name,$places);";
            cmd.Parameters.AddWithValue("$id", id.ToString()); cmd.Parameters.AddWithValue("$co", companyId.ToString()); cmd.Parameters.AddWithValue("$code", unit.Code.Trim());
            cmd.Parameters.AddWithValue("$name", unit.Name.Trim()); cmd.Parameters.AddWithValue("$places", unit.DecimalPlaces); cmd.ExecuteNonQuery(); }
        Audit(c, tx, userId, companyId, null, "UnitCreated", "UnitOfMeasure", id, "code=" + unit.Code.Trim()); tx.Commit(); return id;
    }

    public void UpdateUnit(Guid userId, Guid companyId, UnitOfMeasure unit)
    {
        if (unit.Id == Guid.Empty) throw new ArgumentException("معرف وحدة القياس مطلوب."); ValidateUnit(unit);
        using var c = Open(); using var tx = c.BeginTransaction(); Require(c, tx, userId, companyId, null, "inventory.items.manage");
        using (var check = c.CreateCommand()) { check.Transaction = tx; check.CommandText = "SELECT COUNT(*) FROM monarch_items WHERE company_id=$co AND unit_id=$id;";
            check.Parameters.AddWithValue("$co", companyId.ToString()); check.Parameters.AddWithValue("$id", unit.Id.ToString());
            if (Convert.ToInt64(check.ExecuteScalar()) > 0) { using var old = c.CreateCommand(); old.Transaction = tx; old.CommandText = "SELECT decimal_places FROM monarch_units WHERE company_id=$co AND id=$id;";
                old.Parameters.AddWithValue("$co", companyId.ToString()); old.Parameters.AddWithValue("$id", unit.Id.ToString()); var value = old.ExecuteScalar();
                if (value == null || value == DBNull.Value) throw new InvalidOperationException("وحدة القياس غير موجودة.");
                if (Convert.ToInt32(value) != unit.DecimalPlaces) throw new InvalidOperationException("لا يمكن تغيير دقة وحدة قياس مستخدمة في أصناف؛ أنشئ وحدة جديدة لتجنب تغيير معنى الكميات التاريخية."); } }
        using (var cmd = c.CreateCommand()) { cmd.Transaction = tx; cmd.CommandText = "UPDATE monarch_units SET code=$code,name=$name,decimal_places=$places WHERE company_id=$co AND id=$id;";
            cmd.Parameters.AddWithValue("$code", unit.Code.Trim()); cmd.Parameters.AddWithValue("$name", unit.Name.Trim()); cmd.Parameters.AddWithValue("$places", unit.DecimalPlaces);
            cmd.Parameters.AddWithValue("$co", companyId.ToString()); cmd.Parameters.AddWithValue("$id", unit.Id.ToString()); if (cmd.ExecuteNonQuery() != 1) throw new InvalidOperationException("وحدة القياس غير موجودة."); }
        Audit(c, tx, userId, companyId, null, "UnitUpdated", "UnitOfMeasure", unit.Id, "code=" + unit.Code.Trim()); tx.Commit();
    }

    private static void RequireActiveGroup(SqliteConnection c, SqliteTransaction tx, Guid company, Guid? groupId)
    {
        if (!groupId.HasValue) return; using var q = c.CreateCommand(); q.Transaction = tx; q.CommandText = "SELECT COUNT(*) FROM monarch_item_groups WHERE company_id=$co AND id=$id AND is_active=1;";
        q.Parameters.AddWithValue("$co", company.ToString()); q.Parameters.AddWithValue("$id", groupId.Value.ToString());
        if (Convert.ToInt64(q.ExecuteScalar()) != 1) throw new InvalidOperationException("مجموعة الصنف غير موجودة أو غير نشطة ضمن المنشأة.");
    }
    private static void ValidateGroup(ItemGroup g) { if (string.IsNullOrWhiteSpace(g.Code) || g.Code.Trim().Length > 32 || string.IsNullOrWhiteSpace(g.Name) || g.Name.Trim().Length > 120 || g.ParentId == g.Id && g.Id != Guid.Empty) throw new ArgumentException("بيانات مجموعة الأصناف غير صحيحة."); }
    private static void ValidateUnit(UnitOfMeasure u) { if (string.IsNullOrWhiteSpace(u.Code) || u.Code.Trim().Length > 32 || string.IsNullOrWhiteSpace(u.Name) || u.Name.Trim().Length > 100 || u.DecimalPlaces < 0 || u.DecimalPlaces > 6) throw new ArgumentException("بيانات وحدة القياس غير صحيحة."); }
    private static void ValidateGroupParent(SqliteConnection c, SqliteTransaction tx, Guid company, Guid? parentId, Guid? selfId)
    {
        if (!parentId.HasValue) return; if (parentId == selfId) throw new InvalidOperationException("لا يمكن جعل المجموعة أباً لنفسها.");
        using var q = c.CreateCommand(); q.Transaction = tx; q.CommandText = "SELECT COUNT(*) FROM monarch_item_groups WHERE company_id=$co AND id=$id AND is_active=1;";
        q.Parameters.AddWithValue("$co", company.ToString()); q.Parameters.AddWithValue("$id", parentId.Value.ToString());
        if (Convert.ToInt64(q.ExecuteScalar()) != 1) throw new InvalidOperationException("المجموعة الأب غير موجودة أو غير نشطة في المنشأة.");
        if (selfId.HasValue) { using var cycle = c.CreateCommand(); cycle.Transaction = tx; cycle.CommandText = @"WITH RECURSIVE descendants(id) AS (SELECT id FROM monarch_item_groups WHERE company_id=$co AND parent_id=$self UNION SELECT g.id FROM monarch_item_groups g JOIN descendants d ON g.parent_id=d.id WHERE g.company_id=$co) SELECT COUNT(*) FROM descendants WHERE id=$parent;";
            cycle.Parameters.AddWithValue("$co", company.ToString()); cycle.Parameters.AddWithValue("$self", selfId.Value.ToString()); cycle.Parameters.AddWithValue("$parent", parentId.Value.ToString());
            if (Convert.ToInt64(cycle.ExecuteScalar()) > 0) throw new InvalidOperationException("لا يمكن نقل المجموعة إلى أحد فروعها التابعة."); }
    }

    public Guid CreateItem(Guid userId, Guid companyId, StockItem item)
    {
        ValidateItem(item); var id = item.Id == Guid.Empty ? Guid.NewGuid() : item.Id; var now = DateTimeOffset.UtcNow.ToString("O");
        using var c = Open(); using var tx = c.BeginTransaction(); Require(c, tx, userId, companyId, null, "inventory.items.manage");
        RequireActiveGroup(c, tx, companyId, item.GroupId);
        using (var cmd = c.CreateCommand()) { cmd.Transaction = tx; cmd.CommandText = @"INSERT INTO monarch_items
(id,company_id,group_id,unit_id,sku,barcode,name,item_type,cost_minor,sale_price_minor,reorder_level_milli,allow_negative,is_active,created_utc)
VALUES($id,$co,$group,$unit,$sku,$barcode,$name,$type,$cost,$price,$reorder,$negative,1,$now);";
            cmd.Parameters.AddWithValue("$id", id.ToString()); cmd.Parameters.AddWithValue("$co", companyId.ToString());
            Add(cmd,"$group",item.GroupId?.ToString()); cmd.Parameters.AddWithValue("$unit",item.UnitId.ToString());
            cmd.Parameters.AddWithValue("$sku",item.Sku.Trim()); Add(cmd,"$barcode",Normalize(item.Barcode)); cmd.Parameters.AddWithValue("$name",item.Name.Trim());
            cmd.Parameters.AddWithValue("$type",item.Type.ToString()); cmd.Parameters.AddWithValue("$cost",item.CostMinor); cmd.Parameters.AddWithValue("$price",item.SalePriceMinor);
            cmd.Parameters.AddWithValue("$reorder",item.ReorderLevelMilli); cmd.Parameters.AddWithValue("$negative",item.AllowNegative?1:0); cmd.Parameters.AddWithValue("$now",now); cmd.ExecuteNonQuery(); }
        Audit(c,tx,userId,companyId,null,"InventoryItemCreated","InventoryItem",id,"sku="+item.Sku.Trim()); tx.Commit(); return id;
    }

    public void UpdateItem(Guid userId, Guid companyId, StockItem item)
    {
        if (item.Id == Guid.Empty) throw new ArgumentException("Item id is required.",nameof(item)); ValidateItem(item);
        using var c=Open(); using var tx=c.BeginTransaction(); Require(c,tx,userId,companyId,null,"inventory.items.manage");
        RequireActiveGroup(c, tx, companyId, item.GroupId);
        using(var cmd=c.CreateCommand()) { cmd.Transaction=tx; cmd.CommandText=@"UPDATE monarch_items SET group_id=$group,unit_id=$unit,sku=$sku,barcode=$barcode,name=$name,item_type=$type,cost_minor=$cost,sale_price_minor=$price,reorder_level_milli=$reorder,allow_negative=$negative
WHERE company_id=$co AND id=$id AND is_active=1;";
            cmd.Parameters.AddWithValue("$co",companyId.ToString());cmd.Parameters.AddWithValue("$id",item.Id.ToString());Add(cmd,"$group",item.GroupId?.ToString());cmd.Parameters.AddWithValue("$unit",item.UnitId.ToString());
            cmd.Parameters.AddWithValue("$sku",item.Sku.Trim());Add(cmd,"$barcode",Normalize(item.Barcode));cmd.Parameters.AddWithValue("$name",item.Name.Trim());cmd.Parameters.AddWithValue("$type",item.Type.ToString());
            cmd.Parameters.AddWithValue("$cost",item.CostMinor);cmd.Parameters.AddWithValue("$price",item.SalePriceMinor);cmd.Parameters.AddWithValue("$reorder",item.ReorderLevelMilli);cmd.Parameters.AddWithValue("$negative",item.AllowNegative?1:0);
            if(cmd.ExecuteNonQuery()!=1) throw new InvalidOperationException("الصنف غير موجود أو غير نشط ضمن المنشأة المحددة."); }
        Audit(c,tx,userId,companyId,null,"InventoryItemUpdated","InventoryItem",item.Id,"sku="+item.Sku.Trim());tx.Commit();
    }

    public void SetItemActive(Guid userId, Guid companyId, Guid itemId, bool isActive)
    {
        using var c=Open();using var tx=c.BeginTransaction();Require(c,tx,userId,companyId,null,"inventory.items.manage");
        using(var cmd=c.CreateCommand()){cmd.Transaction=tx;cmd.CommandText="UPDATE monarch_items SET is_active=$active WHERE company_id=$co AND id=$id;";cmd.Parameters.AddWithValue("$active",isActive?1:0);cmd.Parameters.AddWithValue("$co",companyId.ToString());cmd.Parameters.AddWithValue("$id",itemId.ToString());if(cmd.ExecuteNonQuery()!=1)throw new InvalidOperationException("الصنف غير موجود ضمن المنشأة.");}
        Audit(c,tx,userId,companyId,null,isActive?"InventoryItemActivated":"InventoryItemDeactivated","InventoryItem",itemId,null);tx.Commit();
    }

    public Guid CreateWarehouse(Guid userId, Guid companyId, Warehouse warehouse)
    {
        if(warehouse.BranchId==Guid.Empty||string.IsNullOrWhiteSpace(warehouse.Code)||warehouse.Code.Trim().Length>32||string.IsNullOrWhiteSpace(warehouse.Name)||warehouse.Name.Trim().Length>120)throw new ArgumentException("بيانات المستودع غير صحيحة.");
        var id=warehouse.Id==Guid.Empty?Guid.NewGuid():warehouse.Id;var now=DateTimeOffset.UtcNow.ToString("O");using var c=Open();using var tx=c.BeginTransaction();Require(c,tx,userId,companyId,warehouse.BranchId,"inventory.warehouses.manage");
        using(var cmd=c.CreateCommand()){cmd.Transaction=tx;cmd.CommandText=@"INSERT INTO monarch_warehouses(id,company_id,branch_id,code,name,is_active,created_utc) VALUES($id,$co,$br,$code,$name,1,$now);";
            cmd.Parameters.AddWithValue("$id",id.ToString());cmd.Parameters.AddWithValue("$co",companyId.ToString());cmd.Parameters.AddWithValue("$br",warehouse.BranchId.ToString());cmd.Parameters.AddWithValue("$code",warehouse.Code.Trim());cmd.Parameters.AddWithValue("$name",warehouse.Name.Trim());cmd.Parameters.AddWithValue("$now",now);cmd.ExecuteNonQuery();}
        Audit(c,tx,userId,companyId,warehouse.BranchId,"WarehouseCreated","Warehouse",id,"code="+warehouse.Code.Trim());tx.Commit();return id;
    }

    public void UpdateWarehouse(Guid userId, Guid companyId, Warehouse warehouse)
    {
        if(warehouse.Id==Guid.Empty||warehouse.BranchId==Guid.Empty||string.IsNullOrWhiteSpace(warehouse.Code)||warehouse.Code.Trim().Length>32||string.IsNullOrWhiteSpace(warehouse.Name)||warehouse.Name.Trim().Length>120)throw new ArgumentException("بيانات المستودع غير صحيحة.");
        using var c=Open();using var tx=c.BeginTransaction();Require(c,tx,userId,companyId,warehouse.BranchId,"inventory.warehouses.manage");
        using(var cmd=c.CreateCommand()){cmd.Transaction=tx;cmd.CommandText="UPDATE monarch_warehouses SET code=$code,name=$name WHERE company_id=$co AND branch_id=$br AND id=$id AND is_active=1;";cmd.Parameters.AddWithValue("$code",warehouse.Code.Trim());cmd.Parameters.AddWithValue("$name",warehouse.Name.Trim());cmd.Parameters.AddWithValue("$co",companyId.ToString());cmd.Parameters.AddWithValue("$br",warehouse.BranchId.ToString());cmd.Parameters.AddWithValue("$id",warehouse.Id.ToString());if(cmd.ExecuteNonQuery()!=1)throw new InvalidOperationException("المستودع غير موجود أو غير نشط في الفرع المحدد.");}
        Audit(c,tx,userId,companyId,warehouse.BranchId,"WarehouseUpdated","Warehouse",warehouse.Id,"code="+warehouse.Code.Trim());tx.Commit();
    }

    public void SetWarehouseActive(Guid userId, Guid companyId, Guid branchId, Guid warehouseId, bool isActive)
    {
        using var c=Open();using var tx=c.BeginTransaction();Require(c,tx,userId,companyId,branchId,"inventory.warehouses.manage");
        if(!isActive){using var balance=c.CreateCommand();balance.Transaction=tx;balance.CommandText="SELECT COUNT(*) FROM monarch_stock_balances WHERE company_id=$co AND warehouse_id=$wh AND quantity_milli<>0;";balance.Parameters.AddWithValue("$co",companyId.ToString());balance.Parameters.AddWithValue("$wh",warehouseId.ToString());if(Convert.ToInt64(balance.ExecuteScalar())>0)throw new InvalidOperationException("لا يمكن تعطيل مستودع يحتوي على أرصدة مخزنية غير صفرية.");}
        using(var cmd=c.CreateCommand()){cmd.Transaction=tx;cmd.CommandText="UPDATE monarch_warehouses SET is_active=$active WHERE company_id=$co AND branch_id=$br AND id=$id;";cmd.Parameters.AddWithValue("$active",isActive?1:0);cmd.Parameters.AddWithValue("$co",companyId.ToString());cmd.Parameters.AddWithValue("$br",branchId.ToString());cmd.Parameters.AddWithValue("$id",warehouseId.ToString());if(cmd.ExecuteNonQuery()!=1)throw new InvalidOperationException("المستودع غير موجود في الفرع المحدد.");}
        Audit(c,tx,userId,companyId,branchId,isActive?"WarehouseActivated":"WarehouseDeactivated","Warehouse",warehouseId,null);tx.Commit();
    }

    private static void ValidateItem(StockItem i){if(i.UnitId==Guid.Empty||string.IsNullOrWhiteSpace(i.Sku)||i.Sku.Trim().Length>64||string.IsNullOrWhiteSpace(i.Name)||i.Name.Trim().Length>200||i.CostMinor<0||i.SalePriceMinor<0||i.ReorderLevelMilli<0||!Enum.IsDefined(typeof(ItemType),i.Type))throw new ArgumentException("بيانات الصنف غير صحيحة.");}
    private static void Require(SqliteConnection c,SqliteTransaction tx,Guid user,Guid company,Guid? branch,string permission){using var q=c.CreateCommand();q.Transaction=tx;q.CommandText=@"SELECT COUNT(*) FROM monarch_users u JOIN monarch_user_company_access ca ON ca.user_id=u.id AND ca.company_id=$co AND ca.is_active=1
JOIN monarch_role_permissions rp ON rp.permission_code=$permission JOIN monarch_user_roles ur ON ur.user_id=u.id AND ur.role_id=rp.role_id
WHERE u.id=$user AND u.is_active=1 AND EXISTS(SELECT 1 FROM monarch_companies co WHERE co.id=$co AND co.is_active=1)
AND ($branch IS NULL OR EXISTS(SELECT 1 FROM monarch_user_branch_access ba JOIN monarch_branches b ON b.company_id=ba.company_id AND b.id=ba.branch_id WHERE ba.user_id=u.id AND ba.company_id=$co AND ba.branch_id=$branch AND ba.is_active=1 AND b.is_active=1));";
        q.Parameters.AddWithValue("$co",company.ToString());q.Parameters.AddWithValue("$user",user.ToString());q.Parameters.AddWithValue("$permission",permission);Add(q,"$branch",branch?.ToString());if(Convert.ToInt64(q.ExecuteScalar())<1)throw new UnauthorizedAccessException("المستخدم غير نشط أو لا يملك صلاحية إدارة بيانات المخزون في المنشأة/الفرع المحدد.");}
    private static void Audit(SqliteConnection c,SqliteTransaction tx,Guid user,Guid company,Guid? branch,string action,string entity,Guid id,string? details){using var a=c.CreateCommand();a.Transaction=tx;a.CommandText="INSERT INTO monarch_company_audit_log(company_id,branch_id,actor_user_id,action,entity_type,entity_id,details,occurred_utc) VALUES($co,$br,$user,$action,$entity,$id,$details,$now);";a.Parameters.AddWithValue("$co",company.ToString());Add(a,"$br",branch?.ToString());a.Parameters.AddWithValue("$user",user.ToString());a.Parameters.AddWithValue("$action",action);a.Parameters.AddWithValue("$entity",entity);a.Parameters.AddWithValue("$id",id.ToString());Add(a,"$details",details);a.Parameters.AddWithValue("$now",DateTimeOffset.UtcNow.ToString("O"));a.ExecuteNonQuery();}
    private static void Add(SqliteCommand c,string name,string? value)=>c.Parameters.AddWithValue(name,(object?)value??DBNull.Value);
    private static string? Normalize(string? s)=>string.IsNullOrWhiteSpace(s)?null:s.Trim();
    private SqliteConnection Open(){var c=new SqliteConnection(_connectionString);c.Open();using var cmd=c.CreateCommand();cmd.CommandText="PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";cmd.ExecuteNonQuery();return c;}
}
