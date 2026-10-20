using MudBlazor;

namespace KSC.MeetingManagement.Web.Components.Layout;

/// <summary>
/// Application theme. The font is bundled with the app (wwwroot/fonts/Vazirmatn-Variable.woff2, SIL OFL), so no external font host is contacted.
/// </summary>
public static class KscTheme
{
    private static readonly string[] FontStack = ["Vazirmatn", "Tahoma", "Segoe UI", "sans-serif"];

    public static MudTheme Default { get; } = new()
    {
        PaletteLight = new PaletteLight
        {
            Primary = "#4f46e5",
            Secondary = "#0891b2",
            Tertiary = "#db2777",
            Background = "#f6f7fb",
            Surface = "#ffffff",
            AppbarBackground = "#ffffff",
            AppbarText = "#1f2937",
            DrawerBackground = "#ffffff",
            DrawerText = "#374151",
            TextPrimary = "#1f2937",
            TextSecondary = "#6b7280",
            LinesDefault = "#e5e7eb",
            Success = "#059669",
            Warning = "#d97706",
            Error = "#dc2626",
            Info = "#0284c7",
        },
        PaletteDark = new PaletteDark
        {
            Primary = "#818cf8",
            Secondary = "#22d3ee",
            Tertiary = "#f472b6",
            Background = "#0f1117",
            Surface = "#181b23",
            AppbarBackground = "#181b23",
            DrawerBackground = "#181b23",
            TextPrimary = "#e5e7eb",
            TextSecondary = "#9ca3af",
            LinesDefault = "#2a2f3a",
        },
        LayoutProperties = new LayoutProperties { DefaultBorderRadius = "12px", DrawerWidthRight = "260px" },
        Typography = new Typography
        {
            Default = new DefaultTypography { FontFamily = FontStack, FontSize = "0.95rem", LineHeight = "1.75" },
            H1 = new H1Typography { FontFamily = FontStack, FontWeight = "800" },
            H2 = new H2Typography { FontFamily = FontStack, FontWeight = "800" },
            H3 = new H3Typography { FontFamily = FontStack, FontWeight = "800" },
            H4 = new H4Typography { FontFamily = FontStack, FontWeight = "800" },
            H5 = new H5Typography { FontFamily = FontStack, FontWeight = "700" },
            H6 = new H6Typography { FontFamily = FontStack, FontWeight = "700" },
            Button = new ButtonTypography { FontFamily = FontStack, TextTransform = "none", FontWeight = "600" },
        },
    };
}
