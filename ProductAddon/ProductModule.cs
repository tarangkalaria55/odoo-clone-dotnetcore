using Core.OdooEngine;

namespace ProductAddon;

public class StockLotModel : OdooModel
{
    public override string Name => "stock.lot";

    public StockLotModel()
    {
        AddField("name", FieldType.Char, "Lot / Serial Number", required: true, module: "ProductAddon");
        AddField("product_id", FieldType.Many2one, "Product", relation: "product.template", required: true, module: "ProductAddon");
        AddField("ref", FieldType.Char, "Internal Reference", module: "ProductAddon");
        AddField("batch_mrp", FieldType.Float, "Batch MRP ($)", defaultValue: 1000.0, module: "ProductAddon");
        AddField("batch_purchase_price", FieldType.Float, "Batch Purchase Price ($)", defaultValue: 600.0, module: "ProductAddon");
        AddField("batch_max_discount", FieldType.Float, "Max Discount (%)", defaultValue: 15.0, module: "ProductAddon");
    }

    [ApiConstrains("name")]
    public void CheckLotNumber(Dictionary<string, object> record)
    {
        if (record.TryGetValue("name", out var n) && n != null && string.IsNullOrWhiteSpace(n.ToString()))
        {
            throw new ValidationError("Lot or Serial number cannot be empty.");
        }
    }
}

public class ProductTemplateModel : OdooModel
{
    public override string Name => "product.template";

    public ProductTemplateModel()
    {
        AddField("name", FieldType.Char, "Product Name", required: true, module: "ProductAddon");
        AddField("default_code", FieldType.Char, "Internal Reference", module: "ProductAddon");
        AddField("list_price", FieldType.Float, "Sales Price", defaultValue: 0.0, module: "ProductAddon");
        AddField("standard_price", FieldType.Float, "Cost", defaultValue: 0.0, module: "ProductAddon");
        AddField("tracking", FieldType.Selection, "Tracking", defaultValue: "none", selection: new List<SelectionOption>
        {
            new("none", "No Tracking"),
            new("serial", "By Unique Serial Number"),
            new("lot", "By Lots / Batches")
        }, module: "ProductAddon");

        AddField("manufacturer_code", FieldType.Char, "Manufacturer Code", module: "ProductAddon");
        AddField("shelf_life_days", FieldType.Integer, "Shelf Life (Days)", defaultValue: 365, module: "ProductAddon");
    }
}

public class ProductModule : IOdooAddon
{
    public string TechnicalName => "ProductAddon";

    public void RegisterModels(ModelRegistry registry)
    {
        registry.Register(new StockLotModel());
        registry.Register(new ProductTemplateModel());
    }
}
