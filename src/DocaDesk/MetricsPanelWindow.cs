using System.Text.Json;
using DocaDesk.Core.Net;
using DocaDesk.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DocaDesk;

/// <summary>Compact always-available metrics panel driven by device profile (kinds, not hard-coded ids).</summary>
public sealed class MetricsPanelWindow : Window
{
    private readonly AppSession _session;
    private readonly ListView _list = new();
    private readonly TextBlock _status = new() { Opacity = 0.7 };
    private string? _etag;
    private readonly DispatcherTimer _timer;

    public MetricsPanelWindow(AppSession session)
    {
        _session = session;
        Title = "DocaDesk metrics";
        var root = new Grid { Padding = new Thickness(16), RowSpacing = 8 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var title = new TextBlock { Text = "Host metrics (from profile)", FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        Grid.SetRow(title, 0);
        Grid.SetRow(_list, 1);
        Grid.SetRow(_status, 2);
        root.Children.Add(title);
        root.Children.Add(_list);
        root.Children.Add(_status);
        Content = root;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(360, 480));

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(ResolveRefreshSec()) };
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();
        _ = RefreshAsync();
        Closed += (_, _) => _timer.Stop();
    }

    private int ResolveRefreshSec()
    {
        // Prefer capabilities / profile hints when present; protocol default is 10.
        try
        {
            var caps = _session.Capabilities;
            if (caps?.ExtensionData is not null &&
                caps.ExtensionData.TryGetValue("profile", out var profileEl) &&
                profileEl.ValueKind == JsonValueKind.Object &&
                profileEl.TryGetProperty("refreshSec", out var rs) &&
                rs.TryGetInt32(out var sec) && sec >= 2)
            {
                return Math.Clamp(sec, 2, 3600);
            }
        }
        catch { /* ignore */ }
        return 10;
    }

    private async Task RefreshAsync()
    {
        if (_session.Client is null || _session.State != SessionState.Paired)
        {
            _status.Text = "Not paired";
            return;
        }

        try
        {
            var (body, etag, notModified) = await _session.Client.GetProfileAsync(_etag);
            if (notModified)
            {
                _status.Text = $"Unchanged · {_etag}";
                return;
            }
            _etag = etag;
            if (body?.Profile is null)
            {
                _status.Text = "Empty profile";
                return;
            }

            var items = new List<string>();
            using var doc = JsonDocument.Parse(body.Profile.Value.GetRawText());
            var maxItems = 10;
            if (doc.RootElement.TryGetProperty("maxItems", out var mi) && mi.TryGetInt32(out var m))
                maxItems = Math.Clamp(m, 1, 40);
            CollectMetrics(doc.RootElement, items, maxItems);
            _list.ItemsSource = items;
            _status.Text = $"Updated · etag {_etag} · maxItems={maxItems}";
            _timer.Interval = TimeSpan.FromSeconds(ResolveRefreshSec());
        }
        catch (Exception ex)
        {
            _status.Text = ex.Message;
        }
    }

    private static void CollectMetrics(JsonElement node, List<string> items, int maxItems)
    {
        if (items.Count >= maxItems) return;
        if (node.ValueKind == JsonValueKind.Object)
        {
            if (node.TryGetProperty("metrics", out var metrics) && metrics.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in metrics.EnumerateArray())
                {
                    if (items.Count >= maxItems) return;
                    var kind = m.TryGetProperty("kind", out var k) ? k.GetString() : "?";
                    var id = m.TryGetProperty("id", out var i) ? i.GetString() : "";
                    var label = m.TryGetProperty("label", out var l) ? l.GetString() : id;
                    var value = m.TryGetProperty("value", out var v) ? v.ToString() :
                                m.TryGetProperty("text", out var t) ? t.GetString() : "";
                    items.Add($"{kind}: {label} = {value}");
                }
            }

            foreach (var prop in node.EnumerateObject())
                CollectMetrics(prop.Value, items, maxItems);
        }
        else if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in node.EnumerateArray())
                CollectMetrics(el, items, maxItems);
        }
    }
}
