using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using RemoteDeck.App.Services;

namespace RemoteDeck.App.Dialogs;

/// <summary>
/// A saved web connection (a router, Proxmox, Grafana...) shown inside a RemoteDeck tab. Only http and https pages
/// load. It uses its own browser profile, apart from the terminals, so a site's sign-in stays between visits.
/// A connection can opt in to accepting a certificate the system does not trust (typical for self-signed devices);
/// the tab then says so in a banner.
/// </summary>
public partial class WebPageView : UserControl, IDisposable
{
    // One profile for every web tab. Creating the environment once keeps all tabs in the same browser process.
    private static Task<CoreWebView2Environment>? _environment;

    private readonly Uri _address;
    private readonly bool _acceptUntrustedCertificate;
    private bool _started;
    private bool _disposed;

    internal WebPageView(Uri address, bool acceptUntrustedCertificate)
    {
        InitializeComponent();
        _address = address;
        _acceptUntrustedCertificate = acceptUntrustedCertificate;
        AddressText.Text = address.AbsoluteUri;

        if (acceptUntrustedCertificate)
        {
            Warning.Text = "The certificate of this site is not checked for this connection. Only use it for devices you trust on a network you trust.";
            Warning.Visibility = Visibility.Visible;
        }

        Loaded += async (_, _) => await StartAsync();
    }

    /// <summary>The page's own title, for the tab.</summary>
    public event Action<string>? TitleChanged;

    /// <summary>Raised once the browser window exists (and again after it has navigated).</summary>
    public event Action? BrowserReady;

    /// <summary>Whether Back and Forward would do anything now; raised as the page navigates.</summary>
    public event Action<bool, bool>? HistoryChanged;

    /// <summary>
    /// Hides this control's own toolbar and warning. Used in a terminal pane: the browser there is a native window laid
    /// over the pane, and ordinary controls cannot be drawn on top of it, so the pane's header carries the buttons.
    /// </summary>
    public bool ToolbarHidden
    {
        set
        {
            Toolbar.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
            if (value)
            {
                Warning.Visibility = Visibility.Collapsed;
            }
        }
    }

    /// <summary>True when this connection skips the certificate check.</summary>
    public bool AcceptsUntrustedCertificate => _acceptUntrustedCertificate;

    /// <summary>The native window of the browser, or zero before it exists.</summary>
    public IntPtr BrowserHandle => Browser.Handle;

    public void GoBack() => Browser.CoreWebView2?.GoBack();

    public void GoForward() => Browser.CoreWebView2?.GoForward();

    public void Reload() => Browser.CoreWebView2?.Reload();

    public void OpenExternal() => External_Click(this, new RoutedEventArgs());

    private async Task StartAsync()
    {
        if (_started || _disposed)
        {
            return;
        }

        _started = true;
        try
        {
            _environment ??= CoreWebView2Environment.CreateAsync(userDataFolder: Path.Combine(AppPaths.Folder, "WebView2Sites"));
            await Browser.EnsureCoreWebView2Async(await _environment);
        }
        catch (Exception ex)
        {
            _environment = null;
            AddressText.Text = "The web view could not start. RemoteDeck needs the Microsoft Edge WebView2 runtime. " + ex.Message;
            return;
        }

        if (_disposed)
        {
            return;
        }

        var core = Browser.CoreWebView2;

        // Pages from a saved site can link to anything; only web pages may load here.
        core.NavigationStarting += (_, e) =>
        {
            if (!IsWebUri(e.Uri))
            {
                e.Cancel = true;
            }
        };

        // "Open in new window" links stay in this tab.
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            if (IsWebUri(e.Uri))
            {
                core.Navigate(e.Uri);
            }
        };

        core.SourceChanged += (_, _) => Dispatcher.InvokeAsync(UpdateBar);
        core.HistoryChanged += (_, _) => Dispatcher.InvokeAsync(UpdateBar);
        core.DocumentTitleChanged += (_, _) => Dispatcher.InvokeAsync(() =>
        {
            if (!string.IsNullOrWhiteSpace(core.DocumentTitle))
            {
                TitleChanged?.Invoke(core.DocumentTitle);
            }
        });

        if (_acceptUntrustedCertificate)
        {
            core.ServerCertificateErrorDetected += (_, e) =>
            {
                e.Action = CoreWebView2ServerCertificateErrorAction.AlwaysAllow;
            };
        }

        core.Navigate(_address.AbsoluteUri);
        BrowserReady?.Invoke();
    }

    private static bool IsWebUri(string text) =>
        text == "about:blank"
        || (Uri.TryCreate(text, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps));

    private void UpdateBar()
    {
        var core = Browser.CoreWebView2;
        if (core is null || _disposed)
        {
            return;
        }

        BackButton.IsEnabled = core.CanGoBack;
        ForwardButton.IsEnabled = core.CanGoForward;
        HistoryChanged?.Invoke(core.CanGoBack, core.CanGoForward);
        AddressText.Text = core.Source;
    }

    private void Back_Click(object sender, RoutedEventArgs e) => Browser.CoreWebView2?.GoBack();

    private void Forward_Click(object sender, RoutedEventArgs e) => Browser.CoreWebView2?.GoForward();

    private void Reload_Click(object sender, RoutedEventArgs e) => Browser.CoreWebView2?.Reload();

    private void External_Click(object sender, RoutedEventArgs e)
    {
        var target = Browser.CoreWebView2?.Source ?? _address.AbsoluteUri;
        if (IsWebUri(target))
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Browser.Dispose();
    }
}
