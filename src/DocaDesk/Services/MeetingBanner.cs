using DocaDesk.Core;
using DocaDesk.Core.Models;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DocaDesk.Services;

/// <summary>
/// "&lt;name&gt; is controlling this computer — Stop": a small window on top of everything, at the top of the screen,
/// for as long as a person in a meeting controls this machine through its input tools (the hub's <c>meeting.control</c>
/// event, PROTOCOL §23.4). Stop ends it at once: the input tools refuse for a moment (<see cref="DocaDesk.Mcp.MeetingHold"/>)
/// and the hub is told (<c>POST /api/v1/meetings/control/stop</c>), which tells the room. The words are fixed here; only
/// the controller's name comes from the hub.
/// </summary>
public sealed class MeetingBanner
{
    private readonly AppSession _session;
    private Window? _window;
    private TextBlock? _text;
    private string? _grant;

    public MeetingBanner(AppSession session)
    {
        _session = session;
        _session.EventReceived += OnEventAsync;
    }

    private Task OnEventAsync(EventEnvelope ev, CancellationToken ct)
    {
        if (!string.Equals(ev.Type, "meeting.control", StringComparison.OrdinalIgnoreCase)) return Task.CompletedTask;
        if (Meetings.ControlOf(ev.Payload) is not { } c) return Task.CompletedTask;
        App.UiDispatcher?.TryEnqueue(() =>
        {
            if (c.Active) Show(c);
            else if (_grant is null || _grant == c.Grant) { DocaDesk.Mcp.MeetingHold.Release(); Hide(); }
        });
        return Task.CompletedTask;
    }

    private void Show(MeetingControl c)
    {
        _grant = c.Grant;
        if (_window is null) Build();
        _text!.Text = Meetings.BannerText(c);
        _window!.AppWindow.Show();
    }

    private void Hide() { _grant = null; _window?.AppWindow.Hide(); }

    private void Build()
    {
        _text = new TextBlock { Foreground = new SolidColorBrush(Colors.White), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, FontSize = 14 };
        var stop = new Button { Content = "Stop", Margin = new Thickness(16, 0, 0, 0) };
        stop.Click += async (_, _) => await StopAsync();
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(new TextBlock { Text = "●", Foreground = new SolidColorBrush(Colors.White), Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(_text);
        row.Children.Add(stop);
        _window = new Window { Title = "DocaDesk — controlled from a meeting", Content = new Grid { Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 200, 40, 40)), Children = { row } } };
        var aw = _window.AppWindow;
        if (aw.Presenter is OverlappedPresenter p)
        {
            p.IsAlwaysOnTop = true; p.IsResizable = false; p.IsMaximizable = false; p.IsMinimizable = false;
            p.SetBorderAndTitleBar(false, false);
        }
        var area = DisplayArea.Primary.WorkArea;
        var (w, h) = (Math.Min(560, area.Width), 52);
        aw.MoveAndResize(new Windows.Graphics.RectInt32(area.X + (area.Width - w) / 2, area.Y + 8, w, h));
        aw.IsShownInSwitchers = false;
        aw.Closing += (sender, e) => { e.Cancel = true; var stopping = StopAsync(); };   // closing it is a Stop, never a way to hide it
    }

    /// <summary>The person's Stop: input refused now, the hub told, the banner gone when the hub confirms.</summary>
    public async Task StopAsync()
    {
        DocaDesk.Mcp.MeetingHold.Hold(TimeSpan.FromSeconds(10));
        try { if (_session.Client is { } client) await client.StopMeetingControlAsync(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("meeting control stop: " + ex.Message); }
        Hide();
    }
}
