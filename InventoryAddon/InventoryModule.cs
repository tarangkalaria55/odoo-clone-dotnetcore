using Core.OdooEngine;

namespace InventoryAddon;

public class StockWarehouseModel : OdooModel
{
    public override string Name => "stock.warehouse";

    public StockWarehouseModel()
    {
        AddField("name", FieldType.Char, "Warehouse Name", required: true, module: "InventoryAddon");
        AddField("code", FieldType.Char, "Short Code", required: true, module: "InventoryAddon");
    }
}

public class StockQuantModel : OdooModel
{
    public override string Name => "stock.quant";

    public StockQuantModel()
    {
        AddField("product_id", FieldType.Many2one, "Product", relation: "product.template", required: true, module: "InventoryAddon");
        AddField("lot_id", FieldType.Many2one, "Lot / Serial", relation: "stock.lot", module: "InventoryAddon");
        AddField("quantity", FieldType.Float, "Quantity on Hand", defaultValue: 0.0, module: "InventoryAddon");
        AddField("location_name", FieldType.Char, "Location", defaultValue: "WH/Stock", module: "InventoryAddon");
    }
}

public class InventoryModule : IOdooAddon
{
    public string TechnicalName => "InventoryAddon";

    public void RegisterModels(ModelRegistry registry)
    {
        registry.Register(new StockWarehouseModel());
        registry.Register(new StockQuantModel());
    }
}
