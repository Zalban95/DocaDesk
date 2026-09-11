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

    private static void OnInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
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
