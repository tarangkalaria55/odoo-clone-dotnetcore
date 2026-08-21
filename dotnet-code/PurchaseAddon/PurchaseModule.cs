using Core.OdooEngine;

namespace PurchaseAddon;

public class PurchaseOrderLineModel : OdooModel
{
    public override string Name => "purchase.order.line";

    public PurchaseOrderLineModel()
    {
        AddField("order_id", FieldType.Many2one, "Order Reference", relation: "purchase.order", module: "PurchaseAddon");
        AddField("product_id", FieldType.Many2one, "Product Name", relation: "product.template", required: true, module: "PurchaseAddon");
        AddField("lot_id", FieldType.Many2one, "Batch No / Lot", relation: "stock.lot", module: "PurchaseAddon");
        AddField("mrp", FieldType.Float, "MRP ($)", defaultValue: 0.0, module: "PurchaseAddon");
        AddField("product_uom_qty", FieldType.Float, "Quantity", defaultValue: 1.0, module: "PurchaseAddon");
        AddField("price_unit", FieldType.Float, "Purchase Price ($)", defaultValue: 0.0, module: "PurchaseAddon");
        AddField("price_subtotal", FieldType.Float, "Total Price ($)", defaultValue: 0.0, readonlyField: true, compute: "ComputeTotalPrice", module: "PurchaseAddon");
    }

    [ApiOnchange("lot_id")]
    public Dictionary<string, object> OnChangeLot(Dictionary<string, object> values, ModelRegistry registry)
    {
        var res = new Dictionary<string, object>();
        if (values.TryGetValue("lot_id", out var lotIdObj) && lotIdObj != null)
        {
            int lotId = lotIdObj is object[] arr ? Convert.ToInt32(arr[0]) : Convert.ToInt32(lotIdObj);
            var lots = registry.SearchRead("stock.lot", ["batch_mrp", "batch_purchase_price"], [["id", "=", lotId]]);
            if (lots.Count > 0)
            {
                var lot = lots[0];
                if (lot.TryGetValue("batch_mrp", out var mrp)) res["mrp"] = mrp;
                if (lot.TryGetValue("batch_purchase_price", out var pp)) res["price_unit"] = pp;
            }
        }
        return res;
    }

    [ApiDepends("product_uom_qty", "price_unit")]
    public void ComputeTotalPrice(Dictionary<string, object> line)
    {
        var qty = Convert.ToDouble(line.TryGetValue("product_uom_qty", out var q) ? q : 1.0);
        var price = Convert.ToDouble(line.TryGetValue("price_unit", out var p) ? p : 0.0);
        line["price_subtotal"] = qty * price;
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
