using Core.OdooEngine;

namespace InvoicingAddon;

public class AccountMoveLineModel : OdooModel
{
    public override string Name => "account.move.line";

    public AccountMoveLineModel()
    {
        AddField("move_id", FieldType.Many2one, "Invoice Reference", relation: "account.move", module: "InvoicingAddon", ondelete: "cascade");
        AddField("product_id", FieldType.Many2one, "Product", relation: "product.template", required: true, module: "InvoicingAddon");
        AddField("name", FieldType.Char, "Label / Description", module: "InvoicingAddon");
        AddField("lot_id", FieldType.Many2one, "Lot / Batch Selection", relation: "stock.lot", module: "InvoicingAddon");
        AddField("quantity", FieldType.Float, "Quantity", defaultValue: 1.0, module: "InvoicingAddon");
        AddField("price_unit", FieldType.Float, "Unit Price", defaultValue: 0.0, module: "InvoicingAddon");
        AddField("price_subtotal", FieldType.Float, "Subtotal", defaultValue: 0.0, readonlyField: true, module: "InvoicingAddon");

        AddField("line_mrp", FieldType.Float, "MRP ($)", defaultValue: 0.0, module: "InvoicingAddon");
        AddField("line_purchase_price", FieldType.Float, "Purchase Price ($)", defaultValue: 0.0, module: "InvoicingAddon");
        AddField("line_max_discount", FieldType.Float, "Max Discount (%)", defaultValue: 0.0, module: "InvoicingAddon");
    }

    [ApiOnchange("lot_id")]
    public Dictionary<string, object> OnChangeLotId(Dictionary<string, object> values, ModelRegistry registry)
    {
        var res = new Dictionary<string, object>();
        if (values.TryGetValue("lot_id", out var lotIdObj) && lotIdObj != null)
        {
            int lotId = lotIdObj is object[] arr ? Convert.ToInt32(arr[0]) : Convert.ToInt32(lotIdObj);
            var lots = registry.SearchRead("stock.lot", ["batch_mrp", "batch_purchase_price", "batch_max_discount"], [["id", "=", lotId]]);
            if (lots.Count > 0)
            {
                var lot = lots[0];
                if (lot.TryGetValue("batch_mrp", out var mrp)) res["line_mrp"] = mrp;
                if (lot.TryGetValue("batch_purchase_price", out var pp)) res["line_purchase_price"] = pp;
                if (lot.TryGetValue("batch_max_discount", out var md)) res["line_max_discount"] = md;
            }
        }
        return res;
    }

    [ApiDepends("quantity", "price_unit")]
    public void ComputeSubtotal(Dictionary<string, object> line)
    {
        var qty = Convert.ToDouble(line.TryGetValue("quantity", out var q) ? q : 1.0);
        var price = Convert.ToDouble(line.TryGetValue("price_unit", out var p) ? p : 0.0);
        line["price_subtotal"] = qty * price;
    }
}

public class AccountMoveModel : OdooModel
{
    public override string Name => "account.move";

    public AccountMoveModel()
    {
        AddField("name", FieldType.Char, "Invoice Number", required: true, module: "InvoicingAddon");
        AddField("partner_id", FieldType.Many2one, "Partner", relation: "res.partner", required: true, module: "InvoicingAddon");
        AddField("invoice_type", FieldType.Selection, "Type", defaultValue: "out_invoice", selection: new List<SelectionOption>
        {
            new("out_invoice", "Customer Invoice"),
            new("in_invoice", "Vendor Bill")
        }, module: "InvoicingAddon");
        AddField("invoice_line_ids", FieldType.One2many, "Invoice Lines", relation: "account.move.line", inverseName: "move_id", module: "InvoicingAddon");
        AddField("amount_total", FieldType.Float, "Total ($)", defaultValue: 0.0, module: "InvoicingAddon");
        AddField("status", FieldType.Selection, "Status", defaultValue: "draft", selection: new List<SelectionOption>
        {
            new("draft", "Draft"),
            new("posted", "Posted"),
            new("paid", "Paid")
        }, readonlyField: true, module: "InvoicingAddon");
    }

    public virtual object ActionPost(int id, ModelRegistry registry)
    {
        registry.Write("account.move", id, new Dictionary<string, object> { ["status"] = "posted" });
        return new { status = "posted" };
    }

    [ApiOndelete]
    public void UnlinkExceptPosted(Dictionary<string, object> record)
    {
        if (record.TryGetValue("status", out var status) && status?.ToString() != "draft")
        {
            throw new ValidationError("Can't delete a posted or paid invoice.");
        }
    }
}

public class InvoicingModule : IOdooAddon
{
    public string TechnicalName => "InvoicingAddon";

    public void RegisterModels(ModelRegistry registry)
    {
        registry.Register(new AccountMoveLineModel());
        registry.Register(new AccountMoveModel());
    }
}
