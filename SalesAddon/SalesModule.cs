using Core.OdooEngine;

namespace SalesAddon;

public class SaleOrderLineModel : OdooModel
{
    public override string Name => "sale.order.line";

    public SaleOrderLineModel()
    {
        AddField("order_id", FieldType.Many2one, "Order Reference", relation: "sale.order", module: "SalesAddon");
        AddField("product_id", FieldType.Many2one, "Product", relation: "product.template", required: true, module: "SalesAddon");
        AddField("name", FieldType.Char, "Description", module: "SalesAddon");
        AddField("product_uom_qty", FieldType.Float, "Quantity", defaultValue: 1.0, module: "SalesAddon");
        AddField("price_unit", FieldType.Float, "Sales Price", defaultValue: 0.0, module: "SalesAddon");
        AddField("price_subtotal", FieldType.Float, "Subtotal", defaultValue: 0.0, readonlyField: true, module: "SalesAddon");
        AddField("margin", FieldType.Float, "Margin ($)", defaultValue: 0.0, readonlyField: true, compute: "ComputeMargin", module: "SalesAddon");
    }

    [ApiDepends("product_uom_qty", "price_unit", "product_id")]
    public void ComputeMargin(Dictionary<string, object> line, ModelRegistry registry)
    {
        var qty = Convert.ToDouble(line.TryGetValue("product_uom_qty", out var q) ? q : 1.0);
        var price = Convert.ToDouble(line.TryGetValue("price_unit", out var p) ? p : 0.0);
        line["price_subtotal"] = qty * price;

        double cost = 50.0;
        if (line.TryGetValue("product_id", out var pIdObj) && pIdObj != null)
        {
            int pId = pIdObj is object[] arr ? Convert.ToInt32(arr[0]) : Convert.ToInt32(pIdObj);
            var prods = registry.SearchRead("product.template", ["standard_price"], [["id", "=", pId]]);
            if (prods.Count > 0 && prods[0].TryGetValue("standard_price", out var cp))
                cost = Convert.ToDouble(cp);
        }
        line["margin"] = (qty * price) - (qty * cost);
    }
}

public class SaleOrderModel : OdooModel
{
    public override string Name => "sale.order";

    public SaleOrderModel()
    {
        AddField("name", FieldType.Char, "Order Reference", required: true, module: "SalesAddon");
        AddField("partner_id", FieldType.Many2one, "Customer", relation: "res.partner", required: true, module: "SalesAddon");
        AddField("order_line", FieldType.One2many, "Order Lines", relation: "sale.order.line", inverseName: "order_id", module: "SalesAddon");
        AddField("amount_untaxed", FieldType.Float, "Untaxed Amount", defaultValue: 0.0, readonlyField: true, compute: "ComputeTotals", module: "SalesAddon");
        AddField("amount_tax", FieldType.Float, "Taxes", defaultValue: 0.0, readonlyField: true, compute: "ComputeTotals", module: "SalesAddon");
        AddField("amount_total", FieldType.Float, "Total ($)", defaultValue: 0.0, readonlyField: true, compute: "ComputeTotals", module: "SalesAddon");
        AddField("total_margin", FieldType.Float, "Total Profit ($)", defaultValue: 0.0, readonlyField: true, compute: "ComputeTotals", module: "SalesAddon");
        AddField("status", FieldType.Selection, "Status", defaultValue: "draft", selection: new List<SelectionOption>
        {
            new("draft", "Quotation"),
            new("sent", "Quotation Sent"),
            new("sale", "Sales Order"),
            new("done", "Locked"),
            new("cancel", "Cancelled")
        }, readonlyField: true, module: "SalesAddon");
    }

    [ApiDepends("order_line")]
    public void ComputeTotals(Dictionary<string, object> record, ModelRegistry registry)
    {
        if (record.TryGetValue("order_line", out var linesObj) && linesObj is List<Dictionary<string, object>> lines)
        {
            double untaxed = lines.Sum(l => Convert.ToDouble(l.TryGetValue("price_subtotal", out var st) ? st : 0.0));
            double margin = lines.Sum(l => Convert.ToDouble(l.TryGetValue("margin", out var m) ? m : 0.0));
            record["amount_untaxed"] = untaxed;
            record["amount_tax"] = untaxed * 0.10;
            record["amount_total"] = untaxed * 1.10;
            record["total_margin"] = margin;
        }
    }

    public virtual object ActionConfirmSales(int id, Dictionary<string, object> values, ModelRegistry registry)
    {
        registry.Write("sale.order", id, new Dictionary<string, object> { ["status"] = "sale" });

        var partner = values.TryGetValue("partner_id", out var p) ? (p is object[] arr ? arr[0] : p) : 1;
        var amount = values.TryGetValue("amount_total", out var a) ? a : 0.0;

        registry.Create("account.move", new Dictionary<string, object>
        {
            ["name"] = $"INV/SO-{id:D4}",
            ["partner_id"] = Convert.ToInt32(partner),
            ["invoice_type"] = "out_invoice",
            ["amount_total"] = amount,
            ["status"] = "draft"
        });

        return new { status = "confirmed" };
    }
}

public class SalesModule : IOdooAddon
{
    public string TechnicalName => "SalesAddon";

    public void RegisterModels(ModelRegistry registry)
    {
        registry.Register(new SaleOrderLineModel());
        registry.Register(new SaleOrderModel());
    }
}
