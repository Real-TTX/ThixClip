namespace ThixClip.Cloud.Web;

/// <summary>One menu of the switcher at the foot of the menu (MatDesign SHELL-15).</summary>
public sealed record MenuArea(string Id, string Title, string Icon, string Href);

/// <summary>The menus: the clips, the account and, for an administrator, the administration. A page belongs to one of them by its address.</summary>
public static class Menu
{
    public static IReadOnlyList<MenuArea> Areas(bool admin)
    {
        var areas = new List<MenuArea>
        {
            new("clips", "Zwischenablage", "clipboard", "/Clips"),
            new("account", "Konto", "user", "/Account"),
        };
        if (admin)
        {
            areas.Add(new MenuArea("admin", "Administration", "shield", "/Admin/Users"));
        }

        return areas;
    }

    public static string AreaOf(string? path)
    {
        path ??= "/";
        if (Under(path, "/Admin"))
        {
            return "admin";
        }

        return Under(path, "/Account") ? "account" : "clips";
    }

    public static bool Under(string path, string prefix)
        => path.Equals(prefix, StringComparison.OrdinalIgnoreCase) || path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase);
}
