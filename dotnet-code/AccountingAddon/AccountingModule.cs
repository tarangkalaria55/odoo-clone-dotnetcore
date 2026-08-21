using Core.OdooEngine;

namespace AccountingAddon;

public class AccountAccountModel : OdooModel
{
    public override string Name => "account.account";

    public AccountAccountModel()
    {
        AddField("code", FieldType.Char, "Code", required: true, module: "AccountingAddon");
        AddField("name", FieldType.Char, "Account Name", required: true, module: "AccountingAddon");
        AddField("account_type", FieldType.Selection, "Account Type", defaultValue: "asset_current", selection: new List<SelectionOption>
        {
            new("asset_current", "Current Assets"),
            new("liability_current", "Current Liabilities"),
            new("income", "Revenue"),
            new("expense", "Expenses")
        }, module: "AccountingAddon");
        AddField("current_balance", FieldType.Float, "Balance ($)", defaultValue: 0.0, module: "AccountingAddon");
    }
}

public class AccountingModule : IOdooAddon
{
    public string TechnicalName => "AccountingAddon";

    public void RegisterModels(ModelRegistry registry)
    {
        registry.Register(new AccountAccountModel());
    }
}
