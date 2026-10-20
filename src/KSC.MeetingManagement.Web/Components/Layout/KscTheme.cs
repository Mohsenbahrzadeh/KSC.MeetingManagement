using MudBlazor;

namespace KSC.MeetingManagement.Web.Components.Layout;

/// <summary>Application theme. Fonts are a local stack so no external font host is contacted; Vazirmatn is used when installed.</summary>
public static class KscTheme
{
    private static readonly string[] FontStack = ["Vazirmatn", "IRANSans", "Tahoma", "Segoe UI", "sans-serif"];

    public static MudTheme Default { get; } = new()
    {
        PaletteLight = new PaletteLight
        {
            Primary = "#1d4ed8",
            Secondary = "#475467",
            AppbarBackground = "#1d4ed8",
            Background = "#f5f7fa",
        },
        PaletteDark = new PaletteDark
        {
            Primary = "#6ea0ff",
        },
        Typography = new Typography
        {
            Default = new DefaultTypography { FontFamily = FontStack },
            H1 = new H1Typography { FontFamily = FontStack },
            H2 = new H2Typography { FontFamily = FontStack },
            H3 = new H3Typography { FontFamily = FontStack },
            H4 = new H4Typography { FontFamily = FontStack },
            H5 = new H5Typography { FontFamily = FontStack },
            H6 = new H6Typography { FontFamily = FontStack },
            Button = new ButtonTypography { FontFamily = FontStack, TextTransform = "none" },
        },
    };
}
