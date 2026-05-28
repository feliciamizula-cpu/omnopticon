using MudBlazor;

namespace Argus.Web.Themes;

public static class ArgusTheme
{
    public static readonly MudTheme Instance = new()
    {
        PaletteDark = new PaletteDark
        {
            Primary          = "#f5a524",
            PrimaryDarken    = "#c98a1d",
            PrimaryLighten   = "#f7c06a",
            PrimaryContrastText = "#0b0e13",
            Secondary        = "#8a93a3",
            Background       = "#0b0e13",
            Surface          = "#11151c",
            BackgroundGray   = "#161b24",
            AppbarBackground = "#11151c",
            DrawerBackground = "#0b0e13",
            DrawerText       = "#e7ecf3",
            DrawerIcon       = "#8a93a3",
            TextPrimary      = "#e7ecf3",
            TextSecondary    = "#8a93a3",
            TextDisabled     = "#5c6473",
            ActionDefault    = "#8a93a3",
            ActionDisabled   = "#5c6473",
            ActionDisabledBackground = "rgba(92, 100, 115, 0.12)",
            Divider          = "#242b38",
            DividerLight     = "#1a1f29",
            TableLines       = "#1a1f29",
            TableHover       = "rgba(255, 255, 255, 0.04)",
            LinesDefault     = "#242b38",
            LinesInputs      = "#3a4356",
            Error            = "#f87272",
            Warning          = "#f5a524",
            Info             = "#60a5fa",
            Success          = "#36d399",
            Dark             = "#0b0e13",
            DarkLighten      = "#11151c",
        },
        Typography = new Typography
        {
            Default = new DefaultTypography
            {
                FontFamily = ["Inter", "system-ui", "-apple-system", "sans-serif"],
                FontSize   = "13px",
                FontWeight = "400",
            },
            Body1     = new Body1Typography     { FontSize = "13px" },
            Body2     = new Body2Typography     { FontSize = "12px" },
            Caption   = new CaptionTypography   { FontSize = "11px" },
            Button    = new ButtonTypography    { FontSize = "12px", TextTransform = "none", FontWeight = "500" },
            Subtitle1 = new Subtitle1Typography { FontSize = "13px", FontWeight = "600" },
            Subtitle2 = new Subtitle2Typography { FontSize = "11px", FontWeight = "600", LetterSpacing = "0.05em" },
        },
        LayoutProperties = new LayoutProperties
        {
            DefaultBorderRadius = "4px",
        }
    };
}
