using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using RemoteDeck.App.Services;

namespace RemoteDeck.App.Dialogs;

/// <summary>
/// A saved web connection (a router, Proxmox, Grafana...) opened inside RemoteDeck. Only http and https pages
/// load. It uses its own browser profile, apart from the terminals, so a site's sign-in stays between visits.
/// A connection can opt in to accepting a certificate the system does not trust (typical for self-signed devices);
/// the window then says so in a banner.
/// </summary>
public partial class WebWindow : Window
{
    private readonly Uri _address;
    private readonly bool _acceptUntrustedCertificate;

    internal WebWindow(string name, Uri address, bool acceptUntrustedCertificate)
    {
        InitializeComponent();
        _address = address;
        _acceptUntrustedCertificate = acceptUntrustedCertificate;
        Title = name + " (web)";
        AddressText.Text = address.AbsoluteUri;

        if (acceptUntrustedCertificate)
        {
            Warning.Text = "The certificate of this site is not checked for this connection. Only use it for devices you trust on a network you trust.";
            Warning.Visibility = Visibility.Visible;
        }

        Loaded += async (_, _) => await StartAsync();
        Closed += (_, _) => Browser.Dispose();
    }

    private async Task StartAsync()
    {
        try
        {
            var folder = Path.Combine(AppPaths.Folder, "WebView2Sites");
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: folder);
            await Browser.EnsureCoreWebView2Async(environment);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                "The web view could not start. RemoteDeck needs the Microsoft Edge WebView2 runtime.\n\n" + ex.Message,
                "RemoteDeck",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            Close();
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

        // "Open in new window" links stay in this window.
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
                Title = core.DocumentTitle + " (web)";
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
    }

    private static bool IsWebUri(string text) =>
        Uri.TryCreate(text, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps || text == "about:blank");

    private void UpdateBar()
    {
        var core = Browser.CoreWebView2;
        if (core is null)
        {
            return;
        }

        BackButton.IsEnabled = core.CanGoBack;
        ForwardButton.IsEnabled = core.CanGoForward;
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
}
