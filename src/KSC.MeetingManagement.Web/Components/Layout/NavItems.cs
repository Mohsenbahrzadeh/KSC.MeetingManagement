namespace KSC.MeetingManagement.Web.Components.Layout;

/// <summary>One entry of the main menu. Every Href must be served by a page; a test enforces this.</summary>
public sealed record NavItem(string Title, string Href, string Icon, bool ExactMatch = false);

public static class NavItems
{
    public static IReadOnlyList<NavItem> Main { get; } =
    [
        new("خانه", "/", MudBlazor.Icons.Material.Filled.Home, ExactMatch: true),
        new("تقویم", "/calendar", MudBlazor.Icons.Material.Filled.CalendarMonth),
        new("جلسات", "/meetings", MudBlazor.Icons.Material.Filled.Groups),
        new("کمیته‌ها", "/committees", MudBlazor.Icons.Material.Filled.AccountTree),
        new("کاربران", "/users", MudBlazor.Icons.Material.Filled.ManageAccounts),
        new("جست‌وجو", "/search", MudBlazor.Icons.Material.Filled.Search),
    ];
}
