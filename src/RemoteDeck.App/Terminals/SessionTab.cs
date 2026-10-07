using System.ComponentModel;
using System.Windows.Media;
using RemoteDeck.Plugin;

namespace RemoteDeck.App.Terminals;

/// <summary>One open terminal tab: its connection plus what the tab strip shows.</summary>
public sealed class SessionTab : INotifyPropertyChanged
{
    private ConnectionState _state = ConnectionState.Connecting;

    public SessionTab(string title, ITerminalConnection connection)
    {
        Title = title;
        Connection = connection;
    }

    public string Id { get; } = Guid.NewGuid().ToString("N");

    public string Title { get; }

    public ITerminalConnection Connection { get; }

    public ConnectionState State
    {
        get => _state;
        set
        {
            if (_state == value)
            {
                return;
            }

            _state = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(State)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusBrush)));
        }
    }

    public Brush StatusBrush => State switch
    {
        ConnectionState.Connected => Brushes.MediumSeaGreen,
        ConnectionState.Connecting => Brushes.Goldenrod,
        ConnectionState.Failed => Brushes.IndianRed,
        _ => Brushes.Gray,
    };

    public event PropertyChangedEventHandler? PropertyChanged;
}
