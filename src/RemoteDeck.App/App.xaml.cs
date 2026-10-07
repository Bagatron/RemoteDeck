using System.Windows;
using RemoteDeck.App.Services;

namespace RemoteDeck.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // The vault comes first: nothing else is useful until it is open.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var data = AppData.OpenInteractively();
        if (data is null)
        {
            Shutdown();
            return;
        }

        var window = new MainWindow(data);
        MainWindow = window;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        window.Show();
    }
}
