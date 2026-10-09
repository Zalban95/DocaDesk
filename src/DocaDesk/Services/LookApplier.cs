using DocaDesk.Core.Look;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace DocaDesk.Services;

/// <summary>
/// Draws the app's own windows in a <see cref="DeskLook"/>, or back in DocaDesk's own look (null). On the UI thread.
///
/// How: one resource dictionary merged last into the application's resources, holding a brush for each theme
/// resource the look names (the shared fills and the controls' own keys, which WinUI resolves once and so must be named
/// one by one) and the corner radii; every registered window root is then given the look's theme — and flipped
/// through the other one first, which is what makes WinUI look its theme resources up again in a window already
/// drawn. Fonts are set on the elements themselves (the type ramp's styles name their font outright, so a resource
/// cannot reach them): <see cref="Refresh"/> walks a root, and is called again where rows are built in code.
/// </summary>
public static class LookApplier
{
    private static ResourceDictionary? _dict;
    private static readonly List<(WeakReference<FrameworkElement> Root, bool Paint)> _roots = [];
    private static FontFamily? _ui, _uiStrong, _mono;

    public static DeskLook? Current { get; private set; }

    /// <summary>Raised after a look is applied, for what is not a resource (the caption buttons).</summary>
    public static event Action<DeskLook?>? Applied;

    /// <summary>
    /// A window's root to draw in the look. <paramref name="paintBackground"/>: the root has no page background of
    /// its own (a window built in code), so it is given the look's ground.
    /// </summary>
    public static void Register(FrameworkElement root, bool paintBackground = false)
    {
        _roots.RemoveAll(r => !r.Root.TryGetTarget(out _));
        _roots.Add((new WeakReference<FrameworkElement>(root), paintBackground));
        if (Current is not null) ApplyTo(root, paintBackground, Current);
    }

    public static void Apply(DeskLook? look)
    {
        var res = Application.Current.Resources;
        if (_dict is not null) res.MergedDictionaries.Remove(_dict);
        _dict = null;
        Current = look;
        _ui = _uiStrong = _mono = null;
        if (look is not null)
        {
            var d = new ResourceDictionary();
            foreach (var (key, c) in look.Brushes)
                d[key] = new SolidColorBrush(Color.FromArgb(c.A, c.R, c.G, c.B));
            d["ControlCornerRadius"] = new CornerRadius(look.ControlRadius);
            d["OverlayCornerRadius"] = new CornerRadius(look.CardRadius);
            d["DeskCardCornerRadius"] = new CornerRadius(look.CardRadius);
            res.MergedDictionaries.Add(d);
            _dict = d;
            _ui = Font(look.UiFont, strong: false);
            _uiStrong = Font(look.UiFont, strong: true);
            _mono = Font(look.MonoFont, strong: false);
        }
        foreach (var (weak, paint) in _roots.ToList())
            if (weak.TryGetTarget(out var root)) ApplyTo(root, paint, look);
        Applied?.Invoke(look);
    }

    /// <summary>Fonts on what is under a root now (call after building rows in code).</summary>
    public static void Refresh(DependencyObject? root)
    {
        if (root is null) return;
        Walk(root, Current);
    }

    private static void ApplyTo(FrameworkElement root, bool paint, DeskLook? look)
    {
        var target = look is null ? ElementTheme.Default : look.Dark ? ElementTheme.Dark : ElementTheme.Light;
        // Flip through the other theme so WinUI resolves every {ThemeResource} again against the merged dictionary.
        var actual = root.ActualTheme;
        root.RequestedTheme = actual == ElementTheme.Dark ? ElementTheme.Light : ElementTheme.Dark;
        root.RequestedTheme = target;
        if (paint && root is Panel p)
        {
            if (look is not null && look.Brushes.TryGetValue("ApplicationPageBackgroundThemeBrush", out var bg))
                p.Background = new SolidColorBrush(Color.FromArgb(bg.A, bg.R, bg.G, bg.B));
            else
                p.ClearValue(Panel.BackgroundProperty);
        }
        Walk(root, look);
    }

    private static readonly DependencyProperty MarkProperty = DependencyProperty.RegisterAttached(
        "DeskLookFont", typeof(string), typeof(LookApplier), new PropertyMetadata(null));

    /// <summary>
    /// Each TextBlock and control gets the look's font — the code font where it had one (Consolas) — and is marked,
    /// so going back to DocaDesk's own look restores exactly what it had.
    /// </summary>
    private static void Walk(DependencyObject node, DeskLook? look)
    {
        // An icon is drawn with a symbol font: neither it nor what its template holds is text to restyle.
        if (node is IconElement) return;
        switch (node)
        {
            case TextBlock tb: SetFont(tb, TextBlock.FontFamilyProperty, tb.FontFamily, tb.FontWeight.Weight); break;
            case Control c: SetFont(c, Control.FontFamilyProperty, c.FontFamily, c.FontWeight.Weight); break;
        }

        var n = VisualTreeHelper.GetChildrenCount(node);
        if (n == 0 && node is ContentControl { Content: DependencyObject inner }) Walk(inner, look);
        else if (n == 0 && node is Panel panel) foreach (var child in panel.Children) Walk(child, look);
        else if (n == 0 && node is ScrollViewer { Content: DependencyObject sc }) Walk(sc, look);
        else if (n == 0 && node is Border { Child: DependencyObject bc }) Walk(bc, look);
        for (var i = 0; i < n; i++) Walk(VisualTreeHelper.GetChild(node, i), look);
    }

    private static void SetFont(DependencyObject el, DependencyProperty prop, FontFamily current, ushort weight)
    {
        var mark = (string?)el.GetValue(MarkProperty);
        if (mark is null && IsSymbols(current)) return;
        var wasMono = mark is not null ? mark == "mono" : IsMono(current);
        var font = wasMono ? _mono : weight >= 600 ? _uiStrong : _ui;
        if (font is null)
        {
            if (mark is null) return;
            // Back to DocaDesk's own: the code font is set again, the rest from their style.
            if (mark == "mono") el.SetValue(prop, new FontFamily("Consolas"));
            else el.ClearValue(prop);
            el.ClearValue(MarkProperty);
            return;
        }
        el.SetValue(MarkProperty, wasMono ? "mono" : "ui");
        el.SetValue(prop, font);
    }

    private static bool IsSymbols(FontFamily? f) =>
        f?.Source is { } s && (s.Contains("Symbol", StringComparison.OrdinalIgnoreCase)
            || s.Contains("MDL2", StringComparison.OrdinalIgnoreCase) || s.Contains("Fluent Icons", StringComparison.OrdinalIgnoreCase));

    private static bool IsMono(FontFamily? f) =>
        f?.Source is { } s && (s.Contains("Consolas", StringComparison.OrdinalIgnoreCase)
            || s.Contains("Mono", StringComparison.OrdinalIgnoreCase) || s.Contains("Cascadia", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A carried family is loaded from the app's own folder (Assets\Fonts, SIL Open Font License); a strong weight
    /// from its own file, so a heading is drawn in the real semibold rather than one Windows thickens.
    /// </summary>
    private static FontFamily? Font(FontChoice? choice, bool strong)
    {
        if (choice is null) return null;
        if (!choice.Carried) return new FontFamily(choice.Family);
        var file = choice.Family switch
        {
            "IBM Plex Mono" => "IBMPlexMono-Regular.ttf",
            _ => strong ? "IBMPlexSans-SemiBold.ttf" : "IBMPlexSans-Regular.ttf",
        };
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", file);
        if (!File.Exists(path)) return new FontFamily(choice.Family == "IBM Plex Mono" ? "Consolas" : "Segoe UI");
        return new FontFamily($"ms-appx:///Assets/Fonts/{file}#{choice.Family}");
    }
}
