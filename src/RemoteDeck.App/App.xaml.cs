using System.Windows;
using RemoteDeck.App.Services;

namespace RemoteDeck.App;

public partial class App : Application
{
    internal static ThemeManager Themes { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Themes first, so even the unlock dialog already wears your colors.
        Themes = new ThemeManager(AppSettings.Load());
        Themes.Start();
        EventManager.RegisterClassHandler(
            typeof(Window),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) => Themes.StyleWindow((Window)sender)));

        // The vault comes next: nothing else is useful until it is open.
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

    protected override void OnExit(ExitEventArgs e)
    {
        Themes?.Dispose();
        base.OnExit(e);
    }
}
