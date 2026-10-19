using MudBlazor;

namespace Athar.Web.Components;

/// <summary>
/// "The Public Record" (DESIGN.md): ink navy on cool white, cinnabar only for seals, Public Sans with Noto Sans Arabic,
/// one five-step type scale, 6px corners, flat surfaces. Light and dark palettes; CSS tokens live in wwwroot/app.css.
/// </summary>
public static class AtharTheme
{
    private const string Sans = "Public Sans";
    private static readonly string[] Stack = [Sans, "Noto Sans Arabic", "system-ui", "-apple-system", "Segoe UI", "sans-serif"];
    private const string Floating = "0 8px 24px rgb(20 33 61 / 0.14)";

    /// <summary>Component defaults the whole app shares: outlined dense fields with labels above, quiet links, quick tooltips.</summary>
    public static void ApplyDefaults()
    {
        MudGlobal.InputDefaults.Variant = Variant.Outlined;
        MudGlobal.InputDefaults.Margin = Margin.Dense;
        MudGlobal.InputDefaults.ShrinkLabel = true;
        MudGlobal.LinkDefaults.Underline = Underline.Hover;
        MudGlobal.TooltipDefaults.Delay = TimeSpan.FromMilliseconds(400);
        MudGlobal.PopoverDefaults.Elevation = 8;
    }

    public static readonly MudTheme Theme = new()
    {
        PaletteLight = new PaletteLight
        {
            Primary = "#1b2a4a", PrimaryContrastText = "#ffffff", PrimaryDarken = "#101a30", PrimaryLighten = "#2c3f66",
            Secondary = "#c2410c", SecondaryContrastText = "#ffffff",
            Tertiary = "#5b6475", TertiaryContrastText = "#ffffff",
            Info = "#2f5d9e", Success = "#1f7a4d", Warning = "#b45309", Error = "#b42318",
            Dark = "#14213d",
            TextPrimary = "#14213d", TextSecondary = "#5b6475", TextDisabled = "rgba(20,33,61,0.42)",
            ActionDefault = "#5b6475", ActionDisabled = "rgba(20,33,61,0.36)", ActionDisabledBackground = "rgba(20,33,61,0.08)",
            Background = "#f6f7f9", BackgroundGray = "#eef1f5", Surface = "#ffffff",
            DrawerBackground = "#ffffff", DrawerText = "#14213d", DrawerIcon = "#5b6475",
            AppbarBackground = "#ffffff", AppbarText = "#14213d",
            LinesDefault = "#dfe3ea", LinesInputs = "#c6ccd6", TableLines = "#dfe3ea", TableHover = "#eef1f5", TableStriped = "#f6f7f9",
            Divider = "#dfe3ea", DividerLight = "#eef1f5", Skeleton = "rgba(20,33,61,0.08)",
            HoverOpacity = 0.06,
        },
        PaletteDark = new PaletteDark
        {
            Primary = "#a9bde6", PrimaryContrastText = "#0f1420", PrimaryDarken = "#c3d1ef", PrimaryLighten = "#8ea6d6",
            Secondary = "#f07a45", SecondaryContrastText = "#0f1420",
            Tertiary = "#98a2b3", TertiaryContrastText = "#0f1420",
            Info = "#7fa6e8", Success = "#4fbf8a", Warning = "#e7a24a", Error = "#f0796f",
            Dark = "#0b0f18",
            TextPrimary = "#e7eaf0", TextSecondary = "#98a2b3", TextDisabled = "rgba(231,234,240,0.4)",
            ActionDefault = "#98a2b3", ActionDisabled = "rgba(231,234,240,0.34)", ActionDisabledBackground = "rgba(231,234,240,0.08)",
            Background = "#0f1420", BackgroundGray = "#1d2636", Surface = "#161d2b",
            DrawerBackground = "#121826", DrawerText = "#e7eaf0", DrawerIcon = "#98a2b3",
            AppbarBackground = "#121826", AppbarText = "#e7eaf0",
            LinesDefault = "#273042", LinesInputs = "#3a4558", TableLines = "#273042", TableHover = "#1d2636", TableStriped = "#131a27",
            Divider = "#273042", DividerLight = "#1d2636", Skeleton = "rgba(231,234,240,0.08)",
            HoverOpacity = 0.08,
        },
        LayoutProperties = new LayoutProperties
        {
            DefaultBorderRadius = "6px",
            AppbarHeight = "56px",
            DrawerWidthLeft = "240px",
            DrawerWidthRight = "240px",
            DrawerMiniWidthLeft = "64px",
            DrawerMiniWidthRight = "64px",
        },
        // The Flat Record Rule: surfaces in the page are flat; only floating layers (menus, popovers, dialogs) cast the one shadow.
        Shadows = new Shadow { Elevation = [.. Enumerable.Range(0, 26).Select(i => i < 8 ? "none" : Floating)] },
        // The One Scale Rule: 44 (display) / 20 / 16 / 14 / 12, sentence case everywhere.
        Typography = new Typography
        {
            Default = new DefaultTypography { FontFamily = Stack, FontSize = "0.875rem", FontWeight = "400", LineHeight = "1.5", LetterSpacing = "normal" },
            H1 = new H1Typography { FontFamily = Stack, FontSize = "clamp(2rem, 4vw, 2.75rem)", FontWeight = "700", LineHeight = "1.1", LetterSpacing = "-0.01em" },
            H2 = new H2Typography { FontFamily = Stack, FontSize = "1.25rem", FontWeight = "600", LineHeight = "1.3", LetterSpacing = "normal" },
            H3 = new H3Typography { FontFamily = Stack, FontSize = "1.25rem", FontWeight = "600", LineHeight = "1.3", LetterSpacing = "normal" },
            H4 = new H4Typography { FontFamily = Stack, FontSize = "1.25rem", FontWeight = "600", LineHeight = "1.3", LetterSpacing = "normal" },
            H5 = new H5Typography { FontFamily = Stack, FontSize = "1rem", FontWeight = "600", LineHeight = "1.4", LetterSpacing = "normal" },
            H6 = new H6Typography { FontFamily = Stack, FontSize = "1rem", FontWeight = "600", LineHeight = "1.4", LetterSpacing = "normal" },
            Subtitle1 = new Subtitle1Typography { FontFamily = Stack, FontSize = "0.875rem", FontWeight = "600", LineHeight = "1.5", LetterSpacing = "normal" },
            Subtitle2 = new Subtitle2Typography { FontFamily = Stack, FontSize = "0.75rem", FontWeight = "600", LineHeight = "1.4", LetterSpacing = "0.02em" },
            Body1 = new Body1Typography { FontFamily = Stack, FontSize = "0.875rem", FontWeight = "400", LineHeight = "1.5", LetterSpacing = "normal" },
            Body2 = new Body2Typography { FontFamily = Stack, FontSize = "0.875rem", FontWeight = "400", LineHeight = "1.5", LetterSpacing = "normal" },
            Button = new ButtonTypography { FontFamily = Stack, FontSize = "0.875rem", FontWeight = "500", LineHeight = "1.3", LetterSpacing = "normal", TextTransform = "none" },
            Caption = new CaptionTypography { FontFamily = Stack, FontSize = "0.75rem", FontWeight = "400", LineHeight = "1.4", LetterSpacing = "0.01em" },
            Overline = new OverlineTypography { FontFamily = Stack, FontSize = "0.75rem", FontWeight = "500", LineHeight = "1.4", LetterSpacing = "0.02em", TextTransform = "none" },
        },
    };
}
