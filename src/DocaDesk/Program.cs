using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using System.Diagnostics;
using WinRT;

namespace DocaDesk;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        ComWrappersSupport.InitializeComWrappers();

        var key = "DocaDesk";
        var instance = AppInstance.FindOrRegisterForKey(key);
        if (!instance.IsCurrent)
        {
            // Single-instance: redirect activation to the first process and exit.
            RedirectAndExit(instance);
            return;
        }

        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
    }

    private static void RedirectAndExit(AppInstance other)
    {
        try
        {
            var args = AppInstance.GetCurrent().GetActivatedEventArgs();
            other.RedirectActivationToAsync(args).AsTask().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }
}
