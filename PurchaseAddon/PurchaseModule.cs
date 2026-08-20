using Core.OdooEngine;

namespace PurchaseAddon;

public class PurchaseOrderLineModel : OdooModel
{
    public override string Name => "purchase.order.line";

    public PurchaseOrderLineModel()
    {
        AddField("order_id", FieldType.Many2one, "Order Reference", relation: "purchase.order", module: "PurchaseAddon");
        AddField("product_id", FieldType.Many2one, "Product", relation: "product.template", required: true, module: "PurchaseAddon");
        AddField("name", FieldType.Char, "Description", module: "PurchaseAddon");
        AddField("product_uom_qty", FieldType.Float, "Quantity", defaultValue: 1.0, module: "PurchaseAddon");
        AddField("price_unit", FieldType.Float, "Unit Price", defaultValue: 0.0, module: "PurchaseAddon");
        AddField("price_subtotal", FieldType.Float, "Subtotal", defaultValue: 0.0, readonlyField: true, module: "PurchaseAddon");
        
        AddField("custom_spec_note", FieldType.Char, "Custom Specs / Note", module: "PurchaseAddon");
        AddField("discount_tier", FieldType.Selection, "Discount Tier", defaultValue: "standard", selection: new List<SelectionOption>
        {
            new("standard", "Standard (0%)"),
            new("tier1", "Tier 1 (5%)"),
            new("tier2", "Tier 2 (10%)")
        }, module: "PurchaseAddon");
    }

    [ApiDepends("product_uom_qty", "price_unit", "discount_tier")]
    public void ComputeSubtotal(Dictionary<string, object> line)
    {
        var qty = Convert.ToDouble(line.TryGetValue("product_uom_qty", out var q) ? q : 1.0);
        var price = Convert.ToDouble(line.TryGetValue("price_unit", out var p) ? p : 0.0);
        
        double discountMultiplier = 1.0;
        if (line.TryGetValue("discount_tier", out var dt) && dt != null)
        {
            if (dt.ToString() == "tier1") discountMultiplier = 0.95;
            else if (dt.ToString() == "tier2") discountMultiplier = 0.90;
        }

        line["price_subtotal"] = (qty * price) * discountMultiplier;
    }
}

public class PurchaseOrderModel : OdooModel
{
    public override string Name => "purchase.order";

    public PurchaseOrderModel()
    {
        AddField("name", FieldType.Char, "Reference", required: true, module: "PurchaseAddon");
        AddField("partner_id", FieldType.Many2one, "Vendor", relation: "res.partner", required: true, module: "PurchaseAddon");
        AddField("order_line", FieldType.One2many, "Products", relation: "purchase.order.line", inverseName: "order_id", module: "PurchaseAddon");
        AddField("amount_total", FieldType.Float, "Total ($)", defaultValue: 0.0, readonlyField: true, compute: "ComputeTotals", module: "PurchaseAddon");
        AddField("status", FieldType.Selection, "Status", defaultValue: "draft", selection: new List<SelectionOption>
        {
            new("draft", "Request for Quotation"),
            new("purchase", "Purchase Order"),
            new("done", "Locked"),
            new("cancel", "Cancelled")
        }, readonlyField: true, module: "PurchaseAddon");
    }

    [ApiDepends("order_line")]
    public void ComputeTotals(Dictionary<string, object> record, ModelRegistry registry)
    {
        if (record.TryGetValue("order_line", out var linesObj) && linesObj is List<Dictionary<string, object>> lines)
        {
            record["amount_total"] = lines.Sum(l => Convert.ToDouble(l.TryGetValue("price_subtotal", out var st) ? st : 0.0));
        }
    }

    public virtual object ActionConfirmOrder(int id, Dictionary<string, object> values, ModelRegistry registry)
    {
        registry.Write("purchase.order", id, new Dictionary<string, object> { ["status"] = "purchase" });
        return new { status = "confirmed" };
    }
}

public class PurchaseModule : IOdooAddon
{
    public string TechnicalName => "PurchaseAddon";

    public void RegisterModels(ModelRegistry registry)
    {
        registry.Register(new PurchaseOrderLineModel());
        registry.Register(new PurchaseOrderModel());
    }
}
