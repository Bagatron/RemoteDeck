using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using RemoteDeck.Core.Connections;
using RemoteDeck.App.Terminals;

namespace RemoteDeck.App.Dialogs;

/// <summary>
/// A Remote Desktop session inside a RemoteDeck tab, using the Windows Remote Desktop control. The password is handed
/// straight to the control and is never saved. The control is a native window, so it always draws above WPF content
/// that overlaps it; the bar above it carries the status and the Disconnect / Reconnect button instead.
/// </summary>
public partial class RdpSessionView : UserControl, IDisposable
{
    private readonly RdpInfo _info;
    private readonly string _password;
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private readonly DispatcherTimer _resize = new() { Interval = TimeSpan.FromMilliseconds(500) };

    private RdpAxHost? _ax;
    private bool _started;
    private bool _disposed;
    private bool _sawActive;
    private int _polls;

    /// <summary>True when the Windows Remote Desktop control is installed.</summary>
    internal static bool IsAvailable => RdpAxHost.FindClsid() is not null;

    internal RdpSessionView(RdpInfo info, string password)
    {
        InitializeComponent();
        _info = info;
        _password = password;
        StatusText.Text = $"Connecting to {info.Host}...";

        _poll.Tick += (_, _) => Poll();
        _resize.Tick += (_, _) => ApplyResize();
        Loaded += (_, _) => Start();
    }

    private void Start()
    {
        if (_started || _disposed)
        {
            return;
        }

        _started = true;
        var clsid = RdpAxHost.FindClsid();
        if (clsid is null)
        {
            StatusText.Text = "The Windows Remote Desktop control is not available on this computer.";
            ActionButton.Visibility = Visibility.Collapsed;
            return;
        }

        try
        {
            _ax = new RdpAxHost(clsid);
            _ax.HandleCreated += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Background, Connect);
            _ax.SizeChanged += (_, _) =>
            {
                _resize.Stop();
                _resize.Start();
            };
            Host.Child = _ax;
        }
        catch (Exception ex)
        {
            StatusText.Text = "Remote Desktop could not start: " + ex.Message;
            ActionButton.Visibility = Visibility.Collapsed;
        }
    }

    private void Connect()
    {
        if (_ax is null || _disposed)
        {
            return;
        }

        try
        {
            dynamic ocx = _ax.GetOcx()!;
            ocx.Server = _info.Host;

            var user = _info.User ?? string.Empty;
            var domain = _info.Domain ?? string.Empty;
            var slash = user.IndexOf('\\');
            if (slash > 0 && domain.Length == 0)
            {
                domain = user[..slash];
                user = user[(slash + 1)..];
            }

            if (user.Length > 0)
            {
                ocx.UserName = user;
            }

            if (domain.Length > 0)
            {
                ocx.Domain = domain;
            }

            ocx.DesktopWidth = Math.Clamp(_ax.ClientSize.Width, 200, 4096);
            ocx.DesktopHeight = Math.Clamp(_ax.ClientSize.Height, 200, 4096);
            Try(() => ocx.ColorDepth = 32);

            dynamic advanced = AdvancedSettings(ocx)!;
            if ((object?)advanced is not null)
            {
                Try(() => advanced.RDPPort = _info.Port);
                Try(() => advanced.SmartSizing = true);
                Try(() => advanced.EnableCredSspSupport = true);
                Try(() => advanced.Compress = 1);
            }

            if (_password.Length > 0)
            {
                ((IMsTscNonScriptable)_ax.GetOcx()!).put_ClearTextPassword(_password);
            }

            ocx.Connect();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Remote Desktop could not connect: " + ex.Message;
            ActionButton.Content = "Reconnect";
            return;
        }

        _sawActive = false;
        _polls = 0;
        ActionButton.Content = "Disconnect";
        StatusText.Text = $"Connecting to {_info.Host}...";
        _poll.Start();
    }

    /// <summary>The control's advanced settings, from the newest interface it has.</summary>
    private static object? AdvancedSettings(dynamic ocx)
    {
        object? found = null;
        Try(() => found = ocx.AdvancedSettings9);
        if (found is null)
        {
            Try(() => found = ocx.AdvancedSettings8);
        }

        if (found is null)
        {
            Try(() => found = ocx.AdvancedSettings7);
        }

        if (found is null)
        {
            Try(() => found = ocx.AdvancedSettings6);
        }

        if (found is null)
        {
            Try(() => found = ocx.AdvancedSettings5);
        }

        if (found is null)
        {
            Try(() => found = ocx.AdvancedSettings2);
        }

        return found;
    }

    private static void Try(Action action)
    {
        try
        {
            action();
        }
        catch (Exception)
        {
            // A setting the installed control does not have; the session still works without it.
        }
    }

    private int ConnectionState()
    {
        try
        {
            dynamic ocx = _ax!.GetOcx()!;
            return (int)ocx.Connected;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private void Poll()
    {
        if (_ax is null || _disposed)
        {
            return;
        }

        _polls++;
        var state = ConnectionState();
        if (state == 1)
        {
            _sawActive = true;
            StatusText.Text = $"Connected to {_info.Host}";
            return;
        }

        if (state == 2)
        {
            _sawActive = true;
            StatusText.Text = $"Connecting to {_info.Host}...";
            return;
        }

        if (_sawActive || _polls > 6)
        {
            _poll.Stop();
            StatusText.Text = _sawActive
                ? "Disconnected. Use Reconnect to sign in again."
                : "Could not connect. Check the address and sign-in, and that Remote Desktop is turned on for that computer.";
            ActionButton.Content = "Reconnect";
        }
    }

    /// <summary>Tells the remote desktop the new size when the tab is resized; it is scaled to fit in the meantime.</summary>
    private void ApplyResize()
    {
        _resize.Stop();
        if (_ax is null || _disposed || ConnectionState() != 1)
        {
            return;
        }

        var width = (uint)Math.Clamp(_ax.ClientSize.Width, 200, 4096);
        var height = (uint)Math.Clamp(_ax.ClientSize.Height, 200, 4096);
        Try(() =>
        {
            dynamic ocx = _ax.GetOcx()!;
            ocx.UpdateSessionDisplaySettings(width, height, width, height, 0u, 100u, 100u);
        });
    }

    private void Action_Click(object sender, RoutedEventArgs e)
    {
        if (_ax is null)
        {
            return;
        }

        if (ConnectionState() != 0)
        {
            Try(() =>
            {
                dynamic ocx = _ax.GetOcx()!;
                ocx.Disconnect();
            });
            return;
        }

        Connect();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _poll.Stop();
        _resize.Stop();

        if (_ax is not null)
        {
            Try(() =>
            {
                dynamic ocx = _ax.GetOcx()!;
                if ((int)ocx.Connected != 0)
                {
                    ocx.Disconnect();
                }
            });

            Host.Child = null;
            _ax.Dispose();
            _ax = null;
        }

        Host.Dispose();
    }
}
