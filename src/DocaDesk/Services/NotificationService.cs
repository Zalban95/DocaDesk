using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace DocaDesk.Services;

/// <summary>Native toast notifications. Unpackaged: register AUMID best-effort and report failures quietly.</summary>
public static class NotificationService
{
    private static bool _registered;
    private static bool _available = true;

    public static void EnsureRegistered()
    {
        if (_registered) return;
        _registered = true;
        try
        {
            // Unpackaged apps need an AUMID for toasts; Windows App SDK bootstrap may supply one.
            AppNotificationManager.Default.NotificationInvoked += OnInvoked;
            AppNotificationManager.Default.Register();
        }
        catch (Exception ex)
        {
            _available = false;
            System.Diagnostics.Debug.WriteLine("AppNotification register failed (unpackaged?): " + ex.Message);
        }
    }

    public static void ShowPrompt(string promptId, string title, string body)
    {
        if (!AppPrefs.NotifyPrompts) return;
        EnsureRegistered();
        if (!_available)
            return;
        try
        {
            var toast = new AppNotificationBuilder()
                .AddText(title)
                .AddText(body)
                .AddArgument("action", "open-prompt")
                .AddArgument("promptId", promptId)
                .BuildNotification();
            AppNotificationManager.Default.Show(toast);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("ShowPrompt toast failed: " + ex.Message);
        }
    }

    public static void ShowAlert(string title, string body)
    {
        if (!AppPrefs.NotifyAlerts) return;
        EnsureRegistered();
        if (!_available)
            return;
        try
        {
            var toast = new AppNotificationBuilder()
                .AddText(title)
                .AddText(body)
                .AddArgument("action", "alert")
                .BuildNotification();
            AppNotificationManager.Default.Show(toast);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("ShowAlert toast failed: " + ex.Message);
        }
    }

    /// <summary>
    /// A call ringing or about to start (hub meetings): Join opens the room in the panel window — the hub's own
    /// /meet/&lt;id&gt; on the address DocaDesk uses, never an address from the notice.
    /// </summary>
    public static void ShowMeeting(DocaDesk.Core.MeetingRing ring, string title, string body)
    {
        EnsureRegistered();
        if (!_available) return;
        try
        {
            var toast = new AppNotificationBuilder()
                .AddText(string.IsNullOrWhiteSpace(title) ? "A call" : title)
                .AddText(body)
                .AddArgument("action", "join-meeting")
                .AddArgument("meeting", ring.Id)
                .AddButton(new AppNotificationButton("Join").AddArgument("action", "join-meeting").AddArgument("meeting", ring.Id))
                .SetScenario(AppNotificationScenario.IncomingCall)
                .SetTag("meeting-" + ring.Id)
                .BuildNotification();
            AppNotificationManager.Default.Show(toast);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("ShowMeeting toast failed: " + ex.Message);
        }
    }

    private const string AttentionTag = "panel-attention";

    /// <summary>
    /// The panel waits for the person (<see cref="DocaDesk.Core.PanelAttention"/>): fixed words, never the page's,
    /// and clicking opens the window. One at a time — a newer one replaces it.
    /// </summary>
    public static void ShowAttention(string title, string text)
    {
        if (!AppPrefs.NotifyPrompts) return;
        EnsureRegistered();
        if (!_available) return;
        try
        {
            var toast = new AppNotificationBuilder()
                .AddText(title)
                .AddText(text)
                .AddArgument("action", "open-window")
                .SetTag(AttentionTag)
                .BuildNotification();
            AppNotificationManager.Default.Show(toast);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("ShowAttention toast failed: " + ex.Message);
        }
    }

    /// <summary>Take the panel's notification back: answered, or seen in the window.</summary>
    public static void ClearAttention()
    {
        if (!_registered || !_available) return;
        try { _ = AppNotificationManager.Default.RemoveByTagAsync(AttentionTag); } catch { /* already gone */ }
    }

    /// <summary>The hub's certificate no longer matches the one DocaDesk trusts (TODO "A changed hub certificate").</summary>
    public static void ShowCertificateChanged(string host)
    {
        EnsureRegistered();
        if (!_available) return;
        try
        {
            var toast = new AppNotificationBuilder()
                .AddText("DocaDesk cannot connect to DOCA")
                .AddText($"The certificate of {host} has changed, so DocaDesk refuses it. Open DocaDesk to check the new one before you trust it.")
                .AddArgument("action", "open-window")
                .SetTag("certificate")
                .BuildNotification();
            AppNotificationManager.Default.Show(toast);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("certificate toast failed: " + ex.Message);
        }
    }

    private static void OnInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        if (args.Arguments.TryGetValue("action", out var join) && join == "join-meeting"
            && args.Arguments.TryGetValue("meeting", out var meeting) && DocaDesk.Core.Meetings.IsId(meeting))
        {
            App.UiDispatcher?.TryEnqueue(() => { App.ShowMainWindow?.Invoke(); App.OpenMeeting?.Invoke(meeting); });
            return;
        }
        if (args.Arguments.TryGetValue("action", out var open) && open == "open-window")
        {
            App.UiDispatcher?.TryEnqueue(() => App.ShowMainWindow?.Invoke());
            return;
        }
        if (args.Arguments.TryGetValue("action", out var action) &&
            action == "open-prompt" &&
            args.Arguments.TryGetValue("promptId", out var promptId))
        {
            App.UiDispatcher?.TryEnqueue(async () =>
            {
                try
                {
                    var client = App.Session.Client;
                    if (client is null) return;
                    var prompt = await client.GetPromptAsync(promptId);
                    if (prompt is not null)
                        await App.Prompts.ShowPromptAsync(prompt, notify: false);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(ex);
                }
            });
        }
    }
}
