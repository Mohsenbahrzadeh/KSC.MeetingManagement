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
            Primary = "#17324D",
            PrimaryDarken = "#10263B",
            PrimaryLighten = "#365A78",
            Secondary = "#0F766E",
            SecondaryDarken = "#115E59",
            Tertiary = "#526B82",
            Background = "#F4F6F8",
            BackgroundGray = "#EEF1F4",
            Surface = "#ffffff",
            AppbarBackground = "#ffffff",
            AppbarText = "#1E293B",
            DrawerBackground = "#152A3E",
            DrawerText = "#CBD5DF",
            TextPrimary = "#202B36",
            TextSecondary = "#596979",
            LinesDefault = "#D9E0E6",
            TableLines = "#E9EDF1",
            Divider = "#D9E0E6",
            OverlayDark = "rgba(19,35,51,0.58)",
            Success = "#10B981",
            Warning = "#F59E0B",
            Error = "#EF4444",
            Info = "#356B91",
        },
        PaletteDark = new PaletteDark
        {
            Primary = "#A9C2D4",
            PrimaryDarken = "#85A8C0",
            Secondary = "#5CB8AC",
            Tertiary = "#B8C9D6",
            Background = "#121B24",
            BackgroundGray = "#19242F",
            Surface = "#1C2A37",
            AppbarBackground = "#152330",
            AppbarText = "#E7EDF2",
            DrawerBackground = "#0E1821",
            DrawerText = "#CBD5DF",
            TextPrimary = "#E7EDF2",
            TextSecondary = "#A8B6C2",
            LinesDefault = "#344451",
            TableLines = "#293844",
            Divider = "#344451",
        },
        LayoutProperties = new LayoutProperties { DefaultBorderRadius = "8px", DrawerWidthRight = "260px" },
        Typography = new Typography
        {
            Default = new DefaultTypography { FontFamily = FontStack, FontSize = "0.95rem", LineHeight = "1.7" },
            H1 = new H1Typography { FontFamily = FontStack, FontWeight = "700" },
            H2 = new H2Typography { FontFamily = FontStack, FontWeight = "700" },
            H3 = new H3Typography { FontFamily = FontStack, FontWeight = "700" },
            H4 = new H4Typography { FontFamily = FontStack, FontWeight = "700" },
            H5 = new H5Typography { FontFamily = FontStack, FontWeight = "650" },
            H6 = new H6Typography { FontFamily = FontStack, FontWeight = "650" },
            Button = new ButtonTypography { FontFamily = FontStack, TextTransform = "none", FontWeight = "600" },
        },
    };
}
