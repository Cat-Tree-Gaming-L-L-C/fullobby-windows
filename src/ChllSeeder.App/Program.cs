using ChllSeeder.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace ChllSeeder.App;

/// <summary>
/// Custom entry point (DISABLE_XAML_GENERATED_MAIN) so single-instancing runs
/// before XAML starts. Replaces the old Global\EspritSeeder named mutex + pipe.
/// </summary>
public static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        var mainInstance = AppInstance.FindOrRegisterForKey(Branding.SingleInstanceKey);
        if (!mainInstance.IsCurrent)
        {
            // Another instance is running — forward this activation (e.g. a
            // chllseeder:// deep link or a repeat launch) and exit.
            RedirectActivationTo(mainInstance);
            return 0;
        }

        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });

        return 0;
    }

    private static void RedirectActivationTo(AppInstance mainInstance)
    {
        var activationArgs = AppInstance.GetCurrent().GetActivatedEventArgs();

        // RedirectActivationToAsync must not be blocked directly on the STA
        // thread; run it on the thread pool and wait for completion.
        var redirected = new ManualResetEventSlim(false);
        _ = Task.Run(async () =>
        {
            try
            {
                await mainInstance.RedirectActivationToAsync(activationArgs);
            }
            finally
            {
                redirected.Set();
            }
        });
        redirected.Wait(TimeSpan.FromSeconds(10));
    }
}
