using System.Runtime.InteropServices;
using DocaDesk.Core;
using Microsoft.UI.Xaml;

namespace DocaDesk.Services;

/// <summary>
/// When the panel inside a hidden or background window opens a dialog that needs the person — the step-up or switch
/// password, a question, the agent asking to act — say so: a Windows notification that opens the window, and a mark
/// on the tray icon until it is answered. Never while the person is already looking at the panel. The words are
/// <see cref="PanelAttention"/>'s, never the page's.
/// </summary>
public sealed class PanelAttentionHost
{
    private readonly Window _window;
    private readonly TrayHost? _tray;
    private readonly Func<bool> _panelShown;
    private readonly PanelAttention _state = new();

    /// <param name="panelShown">True while the window shows the panel (not DocaDesk's own settings over it).</param>
    public PanelAttentionHost(Window window, TrayHost? tray, DashboardHost dashboard, Func<bool> panelShown)
    {
        _window = window;
        _tray = tray;
        _panelShown = panelShown;
        dashboard.AttentionChanged += kinds => _window.DispatcherQueue.TryEnqueue(() => Apply(_state.Update(kinds, InFront())));
        _window.Activated += (_, e) =>
        {
            if (e.WindowActivationState != WindowActivationState.Deactivated && InFront()) Apply(_state.Shown());
            else _tray?.SetAttention(_state.BadgeWhenHidden);
        };
        _window.AppWindow.Changed += (_, e) =>
        {
            if (e.DidVisibilityChange && !_window.AppWindow.IsVisible) _tray?.SetAttention(_state.BadgeWhenHidden);
        };
    }

    private void Apply(AttentionAction a)
    {
        _tray?.SetAttention(a.Badge);
        if (a.ClearToast) NotificationService.ClearAttention();
        if (a.ToastTitle is not null) NotificationService.ShowAttention(a.ToastTitle, a.ToastText ?? "");
    }

    /// <summary>The person is looking at the panel: the window is visible, in the foreground, and shows the panel.</summary>
    private bool InFront()
    {
        try
        {
            if (!_window.AppWindow.IsVisible || !_panelShown()) return false;
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
            return GetForegroundWindow() == hwnd && !IsIconic(hwnd);
        }
        catch { return false; }
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
}
