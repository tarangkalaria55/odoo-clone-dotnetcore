using System.Security.Claims;
using System.Security.Cryptography;

namespace Core.OdooEngine;

public class BaseUserModel : OdooModel
{
    public override string Name => "res.users";
    public BaseUserModel()
    {
        AddField("name", FieldType.Char, "Name", required: true, module: "base");
        AddField("login", FieldType.Char, "Login", required: true, module: "base");
        AddField("password_hash", FieldType.Char, "Password Hash", module: "base");
        AddField("active", FieldType.Boolean, "Active", defaultValue: true, module: "base");
        AddField("group_ids", FieldType.Many2many, "Groups", relation: "res.groups", module: "base");
    }
}

public class BaseGroupModel : OdooModel
{
    public override string Name => "res.groups";
    public BaseGroupModel()
    {
        AddField("name", FieldType.Char, "Name", required: true, module: "base");
    }
}

public static class PasswordHasher
{
    private const int Iterations = 100_000;
    private const int SaltSize = 16;
    private const int KeySize = 32;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA512, KeySize);
        return $"{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(key)}";
    }

    public static bool Verify(string password, string stored)
    {
        var parts = stored.Split('.');
        if (parts.Length != 3 || !int.TryParse(parts[0], out var iterations)) return false;
        var salt = Convert.FromBase64String(parts[1]);
        var expected = Convert.FromBase64String(parts[2]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA512, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}

public static class SecurityGroups
{
    // Group membership is now a real many2many (res.users.group_ids -> res.groups), so there's
    // no stable "technical name" like Odoo's XML ids (base.group_system) - match by the group's
    // actual `name` instead. UniversalRpcController.Login resolves group_ids to names at login.
    public const string Admin = "Administrator";
    public const string Employee = "Employee";

    public static bool IsAdmin(ClaimsPrincipal user) =>
        (user.FindFirst("groups")?.Value ?? "").Split(',').Contains(Admin);
}

public static class SecuritySeed
{
    public static void EnsureSeedData(ModelRegistry registry)
    {
        var existingGroups = registry.SearchRead("res.groups", ["id", "name"]);
        var adminGroup = existingGroups.FirstOrDefault(g => g["name"]?.ToString() == SecurityGroups.Admin);
        int adminGroupId;

        if (adminGroup != null)
        {
            adminGroupId = Convert.ToInt32(adminGroup["id"]);
        }
        else
        {
            adminGroupId = registry.Create("res.groups", new Dictionary<string, object> { ["name"] = SecurityGroups.Admin });
            registry.Create("res.groups", new Dictionary<string, object> { ["name"] = SecurityGroups.Employee });
        }

        if (registry.SearchRead("res.users", ["id"]).Count == 0)
        {
            registry.Create("res.users", new Dictionary<string, object>
            {
                ["name"] = "Administrator",
                ["login"] = "admin",
                ["password_hash"] = PasswordHasher.Hash("admin"),
                ["group_ids"] = new object[] { adminGroupId }
            });
        }
    }
}
