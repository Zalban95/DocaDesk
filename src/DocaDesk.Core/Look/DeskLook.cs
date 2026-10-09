using System.Globalization;

namespace DocaDesk.Core.Look;

/// <summary>A colour as the app draws it: alpha, red, green, blue.</summary>
public readonly record struct LookColor(byte A, byte R, byte G, byte B)
{
    /// <summary><c>#rgb</c>, <c>#rrggbb</c> or <c>#rrggbbaa</c> (CSS order, alpha last); anything else is null.</summary>
    public static LookColor? Parse(string? hex)
    {
        var s = hex?.Trim();
        if (string.IsNullOrEmpty(s) || s[0] != '#') return null;
        s = s[1..];
        if (s.Length == 3) s = string.Concat(s.Select(c => new string(c, 2)));
        if (s.Length != 6 && s.Length != 8) return null;
        if (!uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return null;
        return s.Length == 6
            ? new LookColor(255, (byte)(v >> 16), (byte)(v >> 8), (byte)v)
            : new LookColor((byte)v, (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8));
    }

    /// <summary>The same colour at a share of its opacity (a hover or pressed shade of the accent).</summary>
    public LookColor WithOpacity(double share) => this with { A = (byte)Math.Round(A * Math.Clamp(share, 0, 1)) };

    public override string ToString() => $"#{A:x2}{R:x2}{G:x2}{B:x2}";
}

/// <summary>A font the look asks for, as this app can draw it: a family it carries, or one Windows has.</summary>
public sealed record FontChoice(string Family, bool Carried);

/// <summary>
/// The panel's look mapped onto WinUI 3: which theme (light or dark), which theme resources take which of the panel's
/// colours, the corners and the fonts. Pure — the app turns it into brushes (Services\LookApplier.cs) — so the mapping
/// is tested here on the hub's own fixtures.
/// </summary>
public sealed class DeskLook
{
    /// <summary>The families this app carries (Assets\Fonts, SIL Open Font License).</summary>
    public static readonly IReadOnlySet<string> CarriedFonts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "IBM Plex Sans", "IBM Plex Mono",
    };

    public required bool Dark { get; init; }
    public required string Etag { get; init; }
    public string? Label { get; init; }
    /// <summary>Theme resource key → colour. Brushes, both the shared fills and the controls' own keys.</summary>
    public required IReadOnlyDictionary<string, LookColor> Brushes { get; init; }
    public double ControlRadius { get; init; } = 4;
    public double CardRadius { get; init; } = 8;
    public FontChoice? UiFont { get; init; }
    public FontChoice? MonoFont { get; init; }

    /// <summary>
    /// The look for a palette, or null when it is no look (no ground, text or accent colour) — then the app keeps its
    /// own. <paramref name="installed"/> says whether Windows has a family (tests pass their own).
    /// </summary>
    public static DeskLook? From(PanelLook? look, Func<string, bool>? installed = null)
    {
        if (look?.Palette is null) return null;
        LookColor? C(string role) => look.Palette.TryGetValue(role, out var v) ? LookColor.Parse(v) : null;
        if (C("bg") is not { } bg || C("text") is not { } text || C("accent") is not { } accent) return null;

        var surface = C("surface") ?? bg;
        var raised = C("raised") ?? surface;
        var dim = C("dim") ?? surface;
        var bg2 = C("bg2") ?? bg;
        var bg3 = C("bg3") ?? raised;
        var border = C("border") ?? C("faint") ?? raised;
        var border2 = C("border2") ?? border;
        var muted = C("muted") ?? text;
        var onAccent = C("onAccent") ?? bg;
        var red = C("red");
        var green = C("green");
        var amber = C("amber");
        var dark = look.Ground switch
        {
            "light" => false,
            "dark" => true,
            _ => Luminance(bg) <= 0.5,
        };

        var b = new Dictionary<string, LookColor>();
        void Set(LookColor c, params string[] keys) { foreach (var k in keys) b[k] = c; }

        // The grounds and cards.
        Set(bg, "ApplicationPageBackgroundThemeBrush", "SolidBackgroundFillColorBaseBrush");
        Set(bg2, "LayerFillColorDefaultBrush", "SolidBackgroundFillColorSecondaryBrush");
        Set(surface, "CardBackgroundFillColorDefaultBrush", "LayerOnMicaBaseAltFillColorDefaultBrush");
        Set(raised, "CardBackgroundFillColorSecondaryBrush", "SolidBackgroundFillColorTertiaryBrush");
        Set(border, "CardStrokeColorDefaultBrush", "DividerStrokeColorDefaultBrush");
        Set(border2, "ControlStrokeColorDefaultBrush", "SurfaceStrokeColorDefaultBrush");
        // Text.
        Set(text, "TextFillColorPrimaryBrush", "ApplicationForegroundThemeBrush");
        Set(muted, "TextFillColorSecondaryBrush", "TextFillColorTertiaryBrush");
        // The accent, and text on it.
        Set(accent, "AccentFillColorDefaultBrush", "AccentTextFillColorPrimaryBrush", "AccentTextFillColorSecondaryBrush",
            "AccentTextFillColorTertiaryBrush", "SystemFillColorAttentionBrush");
        Set(accent.WithOpacity(0.9), "AccentFillColorSecondaryBrush");
        Set(accent.WithOpacity(0.8), "AccentFillColorTertiaryBrush");
        Set(onAccent, "TextOnAccentFillColorPrimaryBrush", "TextOnAccentFillColorSecondaryBrush");
        // States, by the panel's own colours.
        if (red is { } r) Set(r, "SystemFillColorCriticalBrush");
        if (C("bgRed") is { } br) Set(br, "SystemFillColorCriticalBackgroundBrush");
        if (green is { } g) Set(g, "SystemFillColorSuccessBrush");
        if (C("bgGreen") is { } bgr) Set(bgr, "SystemFillColorSuccessBackgroundBrush");
        if (amber is { } a) Set(a, "SystemFillColorCautionBrush");
        if (C("bgAmber") is { } ba) Set(ba, "SystemFillColorCautionBackgroundBrush");
        if (C("bgBlue") is { } bb) Set(bb, "SystemFillColorAttentionBackgroundBrush");
        Set(C("stopped") ?? muted, "SystemFillColorNeutralBrush");

        // The controls' own keys. WinUI's are aliases resolved once (StaticResource), so overriding the shared fill
        // above does not reach them: each control the app draws is named here.
        Set(raised, "ControlFillColorDefaultBrush", "ButtonBackground");
        Set(bg3, "ControlFillColorSecondaryBrush", "ButtonBackgroundPointerOver");
        Set(dim, "ControlFillColorTertiaryBrush", "ButtonBackgroundPressed");
        Set(text, "ButtonForeground", "ButtonForegroundPointerOver");
        Set(muted, "ButtonForegroundPressed");
        Set(border2, "ButtonBorderBrush", "ButtonBorderBrushPointerOver", "ButtonBorderBrushPressed");

        Set(accent, "AccentButtonBackground", "AccentButtonBorderBrush", "AccentButtonBorderBrushPointerOver",
            "AccentButtonBorderBrushPressed");
        Set(accent.WithOpacity(0.9), "AccentButtonBackgroundPointerOver");
        Set(accent.WithOpacity(0.8), "AccentButtonBackgroundPressed");
        Set(onAccent, "AccentButtonForeground", "AccentButtonForegroundPointerOver", "AccentButtonForegroundPressed");

        Set(accent, "ToggleSwitchFillOn", "ToggleSwitchStrokeOn");
        Set(accent.WithOpacity(0.9), "ToggleSwitchFillOnPointerOver");
        Set(accent.WithOpacity(0.8), "ToggleSwitchFillOnPressed");
        Set(onAccent, "ToggleSwitchKnobFillOn", "ToggleSwitchKnobFillOnPointerOver", "ToggleSwitchKnobFillOnPressed");
        Set(muted, "ToggleSwitchStrokeOff", "ToggleSwitchStrokeOffPointerOver", "ToggleSwitchStrokeOffPressed",
            "ToggleSwitchKnobFillOff", "ToggleSwitchKnobFillOffPointerOver", "ToggleSwitchKnobFillOffPressed");
        Set(dim, "ToggleSwitchFillOff");
        Set(raised, "ToggleSwitchFillOffPointerOver", "ToggleSwitchFillOffPressed");

        Set(dim, "TextControlBackground");
        Set(raised, "TextControlBackgroundPointerOver");
        Set(surface, "TextControlBackgroundFocused");
        Set(text, "TextControlForeground", "TextControlForegroundPointerOver", "TextControlForegroundFocused",
            "TextControlHeaderForeground");
        Set(muted, "TextControlPlaceholderForeground", "TextControlPlaceholderForegroundPointerOver",
            "TextControlPlaceholderForegroundFocused");
        Set(border2, "TextControlBorderBrush", "TextControlBorderBrushPointerOver");
        Set(accent, "TextControlBorderBrushFocused");

        Set(accent, "SelectorBarItemPillFill", "CheckBoxCheckBackgroundFillChecked",
            "CheckBoxCheckBackgroundStrokeChecked", "HyperlinkButtonForeground");
        Set(accent.WithOpacity(0.9), "CheckBoxCheckBackgroundFillCheckedPointerOver", "HyperlinkButtonForegroundPointerOver");
        Set(accent.WithOpacity(0.8), "CheckBoxCheckBackgroundFillCheckedPressed", "HyperlinkButtonForegroundPressed");
        Set(onAccent, "CheckBoxCheckGlyphForegroundChecked", "CheckBoxCheckGlyphForegroundCheckedPointerOver",
            "CheckBoxCheckGlyphForegroundCheckedPressed");
        Set(text, "SelectorBarItemForeground", "SelectorBarItemForegroundSelected", "SelectorBarItemForegroundPointerOver");
        Set(muted, "SelectorBarItemForegroundPressed");

        Set(surface, "InfoBarInformationalSeverityBackgroundBrush");
        Set(border, "InfoBarBorderBrush");
        Set(accent, "InfoBarInformationalSeverityIconBackground");
        if (C("bgGreen") is { } sb) Set(sb, "InfoBarSuccessSeverityBackgroundBrush");
        if (green is { } sg) Set(sg, "InfoBarSuccessSeverityIconBackground");
        if (C("bgRed") is { } eb) Set(eb, "InfoBarErrorSeverityBackgroundBrush");
        if (red is { } er) Set(er, "InfoBarErrorSeverityIconBackground");
        if (C("bgAmber") is { } wb) Set(wb, "InfoBarWarningSeverityBackgroundBrush");
        if (amber is { } wa) Set(wa, "InfoBarWarningSeverityIconBackground");

        // Menus and dialogs (the tray's flyout, a ContentDialog).
        Set(surface, "MenuFlyoutPresenterBackground", "ContentDialogBackground", "FlyoutPresenterBackground");
        Set(border, "MenuFlyoutPresenterBorderBrush", "ContentDialogBorderBrush", "FlyoutBorderThemeBrush");
        Set(text, "MenuFlyoutItemForeground", "ContentDialogForeground");
        Set(raised, "MenuFlyoutItemBackgroundPointerOver");

        var installedOrNot = installed ?? (_ => false);
        return new DeskLook
        {
            Dark = dark,
            Etag = string.IsNullOrEmpty(look.Etag) ? string.Join(',', b.Select(kv => kv.Value)) : look.Etag,
            Label = look.ThemeLabel ?? look.Theme,
            Brushes = b,
            ControlRadius = Radius(look.Radius?.Control, 4),
            CardRadius = Radius(look.Radius?.Card ?? look.Radius?.Control, 8),
            UiFont = PickFont(look.Fonts?.Ui, installedOrNot, mono: false),
            MonoFont = PickFont(look.Fonts?.Mono, installedOrNot, mono: true),
        };
    }

    /// <summary>A corner in pixels, kept in reason (the panel's Classic is square, 0).</summary>
    private static double Radius(double? v, double fallback) => v is { } r && r >= 0 && r <= 24 ? r : fallback;

    /// <summary>
    /// The first family in the hub's list this app can draw — one it carries, else one Windows has — as a browser
    /// does; CSS's generic names (system-ui, sans-serif, monospace) are Windows' own UI and code fonts. Null when the
    /// list says nothing this app can use, and the app keeps its font.
    /// </summary>
    public static FontChoice? PickFont(IEnumerable<string>? families, Func<string, bool> installed, bool mono)
    {
        if (families is null) return null;
        foreach (var raw in families)
        {
            var f = raw?.Trim().Trim('"', '\'');
            if (string.IsNullOrEmpty(f)) continue;
            if (CarriedFonts.Contains(f)) return new FontChoice(CarriedFonts.First(c => c.Equals(f, StringComparison.OrdinalIgnoreCase)), true);
            switch (f.ToLowerInvariant())
            {
                case "system-ui" or "-apple-system" or "sans-serif" or "ui-sans-serif":
                    return new FontChoice("Segoe UI", false);
                case "monospace" or "ui-monospace":
                    return new FontChoice("Consolas", false);
                case "serif" or "ui-serif":
                    return new FontChoice("Georgia", false);
            }
            if (installed(f)) return new FontChoice(f, false);
        }
        return mono ? new FontChoice("Consolas", false) : null;
    }

    private static double Luminance(LookColor c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255;
}
