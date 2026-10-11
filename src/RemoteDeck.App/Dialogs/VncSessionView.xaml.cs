using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RemoteDeck.App.Terminals;
using RemoteDeck.Protocols.Vnc;

namespace RemoteDeck.App.Dialogs;

/// <summary>
/// A VNC session inside a RemoteDeck tab or pane, drawn by the built-in viewer (no outside program). The screen is
/// kept in a bitmap that scales to fit; mouse and keyboard go to the remote computer. A bar above the screen shows
/// the status with Disconnect / Reconnect; in a pane the bar only appears when the session has ended.
/// </summary>
public partial class VncSessionView : UserControl, IDisposable
{
    private readonly RdpInfo _info;
    private string? _password;

    private RfbClient? _client;
    private TcpClient? _tcp;
    private WriteableBitmap? _bitmap;
    private byte[] _buffer = Array.Empty<byte>();
    private int _generation;
    private bool _started;
    private bool _disposed;
    private bool _compact;

    private readonly object _pendingGate = new();
    private RfbRect _pending;
    private bool _flushQueued;

    private int _buttons;
    private int _pointerX;
    private int _pointerY;
    private readonly HashSet<uint> _heldKeys = new();
    private readonly HashSet<uint> _heldModifiers = new();
    private readonly Dictionary<Key, uint> _keysDown = new();

    internal VncSessionView(RdpInfo info, string? password)
    {
        InitializeComponent();
        _info = info;
        _password = string.IsNullOrEmpty(password) ? null : password;
        ShowStatus($"Connecting to {info.Host}...");

        Loaded += (_, _) => Start();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                Start();
            }
        };
        PreviewKeyDown += HandleKeyDown;
        PreviewKeyUp += HandleKeyUp;
        TextInput += HandleTextInput;
        LostKeyboardFocus += (_, _) => ReleaseEverything();
    }

    /// <summary>Raised whenever the status text changes. A pane uses it for its header.</summary>
    internal event Action<string>? StatusChanged;

    /// <summary>
    /// Asked for the password when the server wants one and none is saved (or the saved one was refused). Returns what
    /// the user typed, or null if they cancelled. The owner decides whether to remember it.
    /// </summary>
    internal Func<string?>? AskPassword { get; set; }

    /// <summary>For a pane: hides the bar while connected (the pane header shows the status) and shows it over the top of the screen when the session ends.</summary>
    internal bool BarHidden
    {
        set
        {
            _compact = value;
            BarBorder.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private void ShowStatus(string text)
    {
        StatusText.Text = text;
        StatusChanged?.Invoke(text);
    }

    private void OfferReconnect(bool offer)
    {
        ActionButton.Content = offer ? "Reconnect" : "Disconnect";
        KeysButton.IsEnabled = !offer;
        if (_compact)
        {
            BarBorder.Visibility = offer ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void Start()
    {
        if (_started || _disposed)
        {
            return;
        }

        _started = true;
        _ = ConnectAsync();
    }

    private async Task ConnectAsync()
    {
        var generation = ++_generation;
        OfferReconnect(false);
        ShowStatus($"Connecting to {_info.Host}...");

        for (var attempt = 0; attempt < 3; attempt++)
        {
            RfbClient? client = null;
            try
            {
                client = await OpenAsync(_password);
                if (_disposed || generation != _generation)
                {
                    client.Dispose();
                    return;
                }

                _client = client;
                client.Updated += OnUpdated;
                client.Resized += OnResized;
                client.ClipboardReceived += OnClipboard;
                CreateBitmap(client.Width, client.Height);
                Queue(new RfbRect(0, 0, client.Width, client.Height));
                ShowStatus(string.IsNullOrWhiteSpace(client.DesktopName)
                    ? $"Connected to {_info.Host}"
                    : $"Connected to {_info.Host} ({client.DesktopName})");

                var running = client;
                _ = Task.Run(() => Pump(running, generation));
                return;
            }
            catch (VncPasswordRequiredException)
            {
                if (_disposed || generation != _generation)
                {
                    return;
                }

                var typed = AskPassword?.Invoke();
                if (string.IsNullOrEmpty(typed))
                {
                    ShowStatus("This computer needs a password. Use Reconnect to enter it.");
                    OfferReconnect(true);
                    return;
                }

                _password = typed;
            }
            catch (VncAuthenticationException ex)
            {
                _password = null;
                Fail(generation, ex.Message + " Use Reconnect to try again.");
                return;
            }
            catch (RfbException ex)
            {
                Fail(generation, ex.Message);
                return;
            }
            catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException or ObjectDisposedException)
            {
                var reason = ex is OperationCanceledException ? "it did not answer in time" : ex.Message;
                Fail(generation, $"Could not connect to {_info.Host}:{_info.Port}: {reason}");
                return;
            }
        }

        Fail(generation, "Could not sign in.");
    }

    private void Fail(int generation, string text)
    {
        if (_disposed || generation != _generation)
        {
            return;
        }

        ShowStatus(text);
        OfferReconnect(true);
    }

    /// <summary>Connects and signs in. The blocking sign-in runs on a worker thread, with a time limit.</summary>
    private async Task<RfbClient> OpenAsync(string? password)
    {
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            {
                await tcp.ConnectAsync(_info.Host, _info.Port, timeout.Token);
            }

            tcp.ReceiveTimeout = 15000;
            var client = new RfbClient(tcp.GetStream());
            try
            {
                await Task.Run(() => client.Handshake(password));
            }
            catch
            {
                client.Dispose();
                throw;
            }

            tcp.ReceiveTimeout = 0;
            _tcp?.Dispose();
            _tcp = tcp;
            return client;
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    /// <summary>Reads the server's messages on a worker thread until the connection ends.</summary>
    private void Pump(RfbClient client, int generation)
    {
        string reason;
        try
        {
            client.Run();
            reason = "Disconnected.";
        }
        catch (Exception ex) when (ex is RfbException)
        {
            reason = ex.Message;
        }
        catch (Exception)
        {
            reason = "Disconnected.";
        }

        Dispatcher.InvokeAsync(() =>
        {
            if (_disposed || generation != _generation || !ReferenceEquals(client, _client))
            {
                return;
            }

            ReleaseEverything();
            _client = null;
            client.Dispose();
            ShowStatus(reason.EndsWith('.') ? reason + " Use Reconnect to sign in again." : reason + ". Use Reconnect to sign in again.");
            OfferReconnect(true);
        });
    }

    private void CreateBitmap(int width, int height)
    {
        _bitmap = new WriteableBitmap(Math.Max(1, width), Math.Max(1, height), 96, 96, PixelFormats.Bgr32, null);
        _buffer = new byte[Math.Max(1, width) * Math.Max(1, height) * 4];
        Screen.Source = _bitmap;
    }

    // ---- drawing -------------------------------------------------------------------------------------------

    private void OnUpdated(RfbRect area) => Queue(area);

    private void OnResized() => Dispatcher.InvokeAsync(() =>
    {
        if (_client is { } client)
        {
            CreateBitmap(client.Width, client.Height);
            Queue(new RfbRect(0, 0, client.Width, client.Height));
        }
    });

    /// <summary>Remembers that an area needs drawing; many small updates are drawn together.</summary>
    private void Queue(RfbRect area)
    {
        lock (_pendingGate)
        {
            if (_pending.Width == 0 || _pending.Height == 0)
            {
                _pending = area;
            }
            else
            {
                var left = Math.Min(_pending.X, area.X);
                var top = Math.Min(_pending.Y, area.Y);
                var right = Math.Max(_pending.X + _pending.Width, area.X + area.Width);
                var bottom = Math.Max(_pending.Y + _pending.Height, area.Y + area.Height);
                _pending = new RfbRect(left, top, right - left, bottom - top);
            }

            if (_flushQueued)
            {
                return;
            }

            _flushQueued = true;
        }

        Dispatcher.InvokeAsync(Flush, System.Windows.Threading.DispatcherPriority.Render);
    }

    private void Flush()
    {
        RfbRect area;
        lock (_pendingGate)
        {
            area = _pending;
            _pending = default;
            _flushQueued = false;
        }

        if (_disposed || _client is not { } client || _bitmap is null || area.Width <= 0 || area.Height <= 0)
        {
            return;
        }

        if (area.X + area.Width > _bitmap.PixelWidth || area.Y + area.Height > _bitmap.PixelHeight)
        {
            return;
        }

        var copied = client.CopyRegion(area, _buffer);
        if (copied.Width > 0 && copied.Height > 0
            && copied.X + copied.Width <= _bitmap.PixelWidth && copied.Y + copied.Height <= _bitmap.PixelHeight)
        {
            _bitmap.WritePixels(new Int32Rect(copied.X, copied.Y, copied.Width, copied.Height), _buffer, copied.Width * 4, 0);
        }
    }

    private void OnClipboard(string text) => Dispatcher.InvokeAsync(() =>
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch (COMException)
        {
            // Another program has the clipboard open; skip this one.
        }
    });

    // ---- mouse ---------------------------------------------------------------------------------------------

    /// <summary>Where the pointer is on the remote screen, allowing for the bars the picture is centred with.</summary>
    private bool TryRemotePoint(MouseEventArgs e, out int x, out int y)
    {
        x = 0;
        y = 0;
        if (_client is not { } client || client.Width <= 0 || client.Height <= 0 || ScreenHost.ActualWidth <= 0 || ScreenHost.ActualHeight <= 0)
        {
            return false;
        }

        var position = e.GetPosition(ScreenHost);
        var scale = Math.Min(ScreenHost.ActualWidth / client.Width, ScreenHost.ActualHeight / client.Height);
        var offsetX = (ScreenHost.ActualWidth - (client.Width * scale)) / 2;
        var offsetY = (ScreenHost.ActualHeight - (client.Height * scale)) / 2;
        x = Math.Clamp((int)Math.Floor((position.X - offsetX) / scale), 0, client.Width - 1);
        y = Math.Clamp((int)Math.Floor((position.Y - offsetY) / scale), 0, client.Height - 1);
        _pointerX = x;
        _pointerY = y;
        return true;
    }

    private static int ButtonBit(MouseButton button) => button switch
    {
        MouseButton.Left => 1,
        MouseButton.Middle => 2,
        MouseButton.Right => 4,
        _ => 0,
    };

    private void Screen_MouseDown(object sender, MouseButtonEventArgs e)
    {
        Focus();
        Keyboard.Focus(this);
        var bit = ButtonBit(e.ChangedButton);
        if (bit == 0 || !TryRemotePoint(e, out var x, out var y))
        {
            return;
        }

        ScreenHost.CaptureMouse();
        SyncModifiers();
        _buttons |= bit;
        Send(c => c.SendPointer(x, y, _buttons));
        e.Handled = true;
    }

    private void Screen_MouseUp(object sender, MouseButtonEventArgs e)
    {
        var bit = ButtonBit(e.ChangedButton);
        if (bit == 0)
        {
            return;
        }

        _buttons &= ~bit;
        if (_buttons == 0)
        {
            ScreenHost.ReleaseMouseCapture();
        }

        if (TryRemotePoint(e, out var x, out var y))
        {
            Send(c => c.SendPointer(x, y, _buttons));
        }

        e.Handled = true;
    }

    private void Screen_MouseMove(object sender, MouseEventArgs e)
    {
        if (TryRemotePoint(e, out var x, out var y))
        {
            Send(c => c.SendPointer(x, y, _buttons));
        }
    }

    private void Screen_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!TryRemotePoint(e, out var x, out var y) || e.Delta == 0)
        {
            return;
        }

        SyncModifiers();
        var wheel = e.Delta > 0 ? 8 : 16;
        var notches = Math.Clamp(Math.Abs(e.Delta) / 120, 1, 10);
        for (var i = 0; i < notches; i++)
        {
            Send(c =>
            {
                c.SendPointer(x, y, _buttons | wheel);
                c.SendPointer(x, y, _buttons);
            });
        }

        e.Handled = true;
    }

    // ---- keyboard ------------------------------------------------------------------------------------------

    private static readonly Dictionary<Key, uint> SpecialKeys = new()
    {
        [Key.Back] = KeySyms.BackSpace,
        [Key.Tab] = KeySyms.Tab,
        [Key.Return] = KeySyms.Return,
        [Key.Escape] = KeySyms.Escape,
        [Key.Insert] = KeySyms.Insert,
        [Key.Delete] = KeySyms.Delete,
        [Key.Home] = KeySyms.Home,
        [Key.End] = KeySyms.End,
        [Key.PageUp] = KeySyms.PageUp,
        [Key.PageDown] = KeySyms.PageDown,
        [Key.Left] = KeySyms.Left,
        [Key.Up] = KeySyms.Up,
        [Key.Right] = KeySyms.Right,
        [Key.Down] = KeySyms.Down,
        [Key.CapsLock] = KeySyms.CapsLock,
    };

    private static bool IsModifier(Key key) =>
        key is Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin;

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKeyW(uint code, uint mapType);

    /// <summary>The key symbol for a key that is not typed text, or for a letter/number/symbol key used with Ctrl or Alt. Zero when it has none.</summary>
    private static uint KeySymFor(Key key, bool asShortcut)
    {
        if (SpecialKeys.TryGetValue(key, out var special))
        {
            return special;
        }

        if (key is >= Key.F1 and <= Key.F12)
        {
            return KeySyms.Function(key - Key.F1 + 1);
        }

        if (!asShortcut && key != Key.Space)
        {
            return 0;
        }

        if (key == Key.Space)
        {
            return 0x20;
        }

        // The unshifted character on this keyboard layout (a shortcut is always the plain key, such as Ctrl+C).
        var character = MapVirtualKeyW((uint)KeyInterop.VirtualKeyFromKey(key), 2);
        if (character == 0 || (character & 0x80000000) != 0)
        {
            return 0;
        }

        return KeySyms.FromCodePoint((int)char.ToLowerInvariant((char)character));
    }

    private void HandleKeyDown(object sender, KeyEventArgs e)
    {
        if (_client is null)
        {
            return;
        }

        var key = e.Key switch
        {
            Key.System => e.SystemKey,
            Key.ImeProcessed => e.ImeProcessedKey,
            _ => e.Key,
        };

        if (IsModifier(key) || key == Key.DeadCharProcessed)
        {
            // Modifiers are sent when a key that needs them is pressed.
            e.Handled = true;
            return;
        }

        var modifiers = Keyboard.Modifiers;
        var altGr = (modifiers & ModifierKeys.Control) != 0 && (modifiers & ModifierKeys.Alt) != 0;

        // Ctrl+Alt+End is Ctrl+Alt+Delete, as in Remote Desktop.
        if (key == Key.End && altGr)
        {
            SendCtrlAltDel();
            e.Handled = true;
            return;
        }

        var shortcut = !altGr && (modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) != 0;
        var keySym = KeySymFor(key, shortcut);
        if (keySym == 0)
        {
            // Typed text arrives through TextInput instead.
            return;
        }

        e.Handled = true;
        if (_keysDown.ContainsKey(key) && keySym != 0 && _heldKeys.Contains(keySym))
        {
            // Key repeat: send another press, as a held key on a real keyboard does.
            Send(c => c.SendKey(keySym, true));
            return;
        }

        // Ctrl+V pastes what is on this computer's clipboard: hand it to the remote clipboard first.
        if (keySym == 'v' && (modifiers & ModifierKeys.Control) != 0 && (modifiers & ModifierKeys.Alt) == 0)
        {
            SendLocalClipboard();
        }

        SyncModifiers();
        _keysDown[key] = keySym;
        _heldKeys.Add(keySym);
        Send(c => c.SendKey(keySym, true));
    }

    private void HandleKeyUp(object sender, KeyEventArgs e)
    {
        if (_client is null)
        {
            return;
        }

        var key = e.Key switch
        {
            Key.System => e.SystemKey,
            Key.ImeProcessed => e.ImeProcessedKey,
            _ => e.Key,
        };

        if (_keysDown.Remove(key, out var keySym))
        {
            _heldKeys.Remove(keySym);
            Send(c => c.SendKey(keySym, false));
            e.Handled = true;
        }

        if (IsModifier(key))
        {
            ReleaseUnheldModifiers();
            e.Handled = true;
        }
    }

    private void HandleTextInput(object sender, TextCompositionEventArgs e)
    {
        if (_client is null || string.IsNullOrEmpty(e.Text))
        {
            return;
        }

        foreach (var rune in e.Text.EnumerateRunes())
        {
            if (rune.Value < 0x20 || rune.Value == 0x7F)
            {
                continue;
            }

            var keySym = KeySyms.FromCodePoint(rune.Value);
            Send(c =>
            {
                c.SendKey(keySym, true);
                c.SendKey(keySym, false);
            });
        }

        e.Handled = true;
    }

    /// <summary>Presses whichever of Shift, Ctrl, Alt and Windows are down on this keyboard and are not yet down on the remote one.</summary>
    private void SyncModifiers()
    {
        var modifiers = Keyboard.Modifiers;
        Hold((modifiers & ModifierKeys.Shift) != 0, KeySyms.ShiftLeft);
        Hold((modifiers & ModifierKeys.Control) != 0, KeySyms.ControlLeft);
        Hold((modifiers & ModifierKeys.Alt) != 0, KeySyms.AltLeft);
        Hold((modifiers & ModifierKeys.Windows) != 0, KeySyms.SuperLeft);
    }

    private void Hold(bool wanted, uint keySym)
    {
        if (wanted && _heldModifiers.Add(keySym))
        {
            Send(c => c.SendKey(keySym, true));
        }
    }

    /// <summary>Releases the remote modifiers that are no longer held on this keyboard.</summary>
    private void ReleaseUnheldModifiers()
    {
        var modifiers = Keyboard.Modifiers;
        Release((modifiers & ModifierKeys.Shift) == 0, KeySyms.ShiftLeft);
        Release((modifiers & ModifierKeys.Control) == 0, KeySyms.ControlLeft);
        Release((modifiers & ModifierKeys.Alt) == 0, KeySyms.AltLeft);
        Release((modifiers & ModifierKeys.Windows) == 0, KeySyms.SuperLeft);
    }

    private void Release(bool unwanted, uint keySym)
    {
        if (unwanted && _heldModifiers.Remove(keySym))
        {
            Send(c => c.SendKey(keySym, false));
        }
    }

    /// <summary>Lets go of every key and button, so nothing stays pressed on the remote computer when focus moves away.</summary>
    private void ReleaseEverything()
    {
        if (_client is not null)
        {
            foreach (var keySym in _heldKeys.ToArray())
            {
                Send(c => c.SendKey(keySym, false));
            }

            foreach (var keySym in _heldModifiers.ToArray())
            {
                Send(c => c.SendKey(keySym, false));
            }

            if (_buttons != 0)
            {
                Send(c => c.SendPointer(_pointerX, _pointerY, 0));
            }
        }

        _heldKeys.Clear();
        _heldModifiers.Clear();
        _keysDown.Clear();
        _buttons = 0;
        ScreenHost.ReleaseMouseCapture();
    }

    private void SendLocalClipboard()
    {
        try
        {
            if (Clipboard.ContainsText())
            {
                var text = Clipboard.GetText();
                Send(c => c.SendClipboard(text));
            }
        }
        catch (COMException)
        {
            // The clipboard is busy; the paste then uses whatever the remote clipboard already holds.
        }
    }

    private void SendCtrlAltDel()
    {
        Send(c =>
        {
            c.SendKey(KeySyms.ControlLeft, true);
            c.SendKey(KeySyms.AltLeft, true);
            c.SendKey(KeySyms.Delete, true);
            c.SendKey(KeySyms.Delete, false);
            c.SendKey(KeySyms.AltLeft, false);
            c.SendKey(KeySyms.ControlLeft, false);
        });
    }

    /// <summary>Sends to the server; a broken connection is noticed by the reading thread, so errors here are ignored.</summary>
    private void Send(Action<RfbClient> action)
    {
        if (_client is not { } client)
        {
            return;
        }

        try
        {
            action(client);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
        {
            // The reading thread reports the dropped connection.
        }
    }

    // ---- bar -----------------------------------------------------------------------------------------------

    private void Keys_Click(object sender, RoutedEventArgs e)
    {
        SendCtrlAltDel();
        Keyboard.Focus(this);
    }

    private void Action_Click(object sender, RoutedEventArgs e)
    {
        if (_client is not null)
        {
            Disconnect("Disconnected. Use Reconnect to sign in again.");
            return;
        }

        _ = ConnectAsync();
    }

    private void Disconnect(string status)
    {
        _generation++;
        ReleaseEverything();
        var client = _client;
        _client = null;
        client?.Dispose();
        _tcp?.Dispose();
        _tcp = null;
        ShowStatus(status);
        OfferReconnect(true);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _generation++;
        var client = _client;
        _client = null;
        client?.Dispose();
        _tcp?.Dispose();
        _tcp = null;
    }
}
