using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Highlighting;
using Microsoft.Win32;
using RemoteDeck.Core.Notes;

namespace RemoteDeck.App.Dialogs;

/// <summary>
/// The built-in editor: plain text files in tabs with syntax colouring and a soft sticky-note colour per tab. Files are
/// saved as you type (a moment after you stop) and the open tabs come back next time.
/// </summary>
public sealed class NotesView : UserControl, IDisposable
{
    private const long MaxBytes = 8 * 1024 * 1024;

    private NoteLibrary _library;
    private List<string> _closed = new();
    private readonly List<NoteDoc> _docs = new();
    private readonly TabControl _tabs = new() { Padding = new Thickness(0), BorderThickness = new Thickness(0) };
    private readonly TextBlock _message = new() { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly bool _dark;
    private bool _bright = true;
    private bool _restoring = true;
    private bool _disposed;

    internal NotesView(string folder, string? extraFile)
    {
        _library = new NoteLibrary(folder);
        _dark = IsDarkTheme();

        var bar = new DockPanel { Margin = new Thickness(8, 6, 8, 6), LastChildFill = true };
        Button Make(string text, RoutedEventHandler click)
        {
            var button = new Button { Content = text, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 6, 0) };
            button.Click += click;
            DockPanel.SetDock(button, Dock.Left);
            bar.Children.Add(button);
            return button;
        }

        Make("New", (_, _) => NewNote());
        Make("Open...", (_, _) => OpenFiles());
        Make("Save as...", (_, _) => SaveAs());
        var color = Make("Color", (_, _) => { });
        color.Click += (_, _) =>
        {
            if (Active is { } doc)
            {
                var menu = ColorMenu(doc);
                menu.PlacementTarget = color;
                menu.IsOpen = true;
            }
        };
        _message.Foreground = Resource("TextDim", Brushes.Gray);
        bar.Children.Add(_message);

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(bar, 0);
        Grid.SetRow(_tabs, 1);
        grid.Children.Add(bar);
        grid.Children.Add(_tabs);
        Content = grid;

        _tabs.SelectionChanged += (_, _) =>
        {
            if (!_restoring)
            {
                SaveSession();
                Active?.Editor.Focus();
            }
        };
        PreviewKeyDown += OnKey;

        var session = _library.Load();
        _bright = session.Bright;
        _closed = session.Closed;
        foreach (var tab in session.Tabs)
        {
            AddDoc(tab.Path, tab.Color, tab.Ink);
        }

        if (extraFile is not null)
        {
            OpenPath(extraFile);
        }

        if (_docs.Count == 0)
        {
            NewNote();
        }
        else
        {
            _tabs.SelectedIndex = extraFile is null ? Math.Clamp(session.Active, 0, _docs.Count - 1) : _docs.Count - 1;
        }

        _restoring = false;
        SaveSession();
    }

    private NoteDoc? Active => _tabs.SelectedIndex >= 0 && _tabs.SelectedIndex < _docs.Count ? _docs[_tabs.SelectedIndex] : null;

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.S:
                Active?.Save();
                e.Handled = true;
                break;
            case Key.N:
                NewNote();
                e.Handled = true;
                break;
            case Key.W:
                if (Active is { } doc)
                {
                    CloseDoc(doc);
                }

                e.Handled = true;
                break;
        }
    }

    private void NewNote()
    {
        try
        {
            AddDoc(_library.CreateNote(), NoteColors.ForIndex(_docs.Count));
            _tabs.SelectedIndex = _docs.Count - 1;
            SaveSession();
            Active?.Editor.Focus();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Tell("Could not create a note: " + ex.Message);
        }
    }

    private void OpenFiles()
    {
        var dialog = new OpenFileDialog { Multiselect = true, Title = "Open files" };
        if (Directory.Exists(_library.Folder))
        {
            dialog.InitialDirectory = _library.Folder;
        }

        if (dialog.ShowDialog() == true)
        {
            foreach (var file in dialog.FileNames)
            {
                OpenPath(file);
            }

            SaveSession();
        }
    }

    private void OpenPath(string path)
    {
        var existing = _docs.FindIndex(d => string.Equals(d.Path, path, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0)
        {
            _tabs.SelectedIndex = existing;
            return;
        }

        if (!File.Exists(path))
        {
            Tell($"\"{path}\" does not exist.");
            return;
        }

        if (new FileInfo(path).Length > MaxBytes)
        {
            Tell("That file is larger than 8 MB; open it in a real editor.");
            return;
        }

        AddDoc(path, NoteColors.ForIndex(_docs.Count));
        _tabs.SelectedIndex = _docs.Count - 1;
    }

    private void SaveAs()
    {
        if (Active is not { } doc)
        {
            return;
        }

        var dialog = new SaveFileDialog { FileName = System.IO.Path.GetFileName(doc.Path), Title = "Save a copy as" };
        if (Directory.Exists(System.IO.Path.GetDirectoryName(doc.Path)))
        {
            dialog.InitialDirectory = System.IO.Path.GetDirectoryName(doc.Path);
        }

        if (dialog.ShowDialog() == true)
        {
            doc.Retarget(dialog.FileName);
            SaveSession();
        }
    }

    private void AddDoc(string path, string color, string ink = "auto")
    {
        _closed.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        var doc = new NoteDoc(this, path, NoteColors.Normalize(color)) { Ink = NoteColors.NormalizeInk(ink) };
        if (doc.Failed)
        {
            return;
        }

        _docs.Add(doc);
        _tabs.Items.Add(doc.Item);
        doc.ApplyColor();
    }

    private void CloseDoc(NoteDoc doc)
    {
        doc.Save();
        _closed.Add(doc.Path);
        var index = _docs.IndexOf(doc);
        _docs.Remove(doc);
        _tabs.Items.Remove(doc.Item);
        doc.Dispose();
        if (_docs.Count == 0)
        {
            NewNote();
        }
        else
        {
            _tabs.SelectedIndex = Math.Min(index, _docs.Count - 1);
        }

        SaveSession();
    }

    private ContextMenu ColorMenu(NoteDoc doc)
    {
        var menu = new ContextMenu();
        foreach (var name in NoteColors.Names)
        {
            var swatch = new Border
            {
                Width = 14, Height = 14, CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 0, 6, 0),
                Background = Solid(NoteColors.Hex(name, false)),
            };
            var item = new MenuItem
            {
                Header = new StackPanel { Orientation = Orientation.Horizontal, Children = { swatch, new TextBlock { Text = char.ToUpper(name[0]) + name[1..] } } },
                IsChecked = doc.Color == name,
            };
            var chosen = name;
            item.Click += (_, _) =>
            {
                doc.Color = chosen;
                doc.ApplyColor();
                SaveSession();
            };
            menu.Items.Add(item);
        }

        menu.Items.Add(new Separator());

        var inkMenu = new MenuItem { Header = "Text color" };
        foreach (var name in NoteColors.InkNames)
        {
            var preview = name == "auto" ? null : Solid(NoteColors.InkHex(name, true, "#ECEFF4"));
            var swatch = new Border
            {
                Width = 14, Height = 14, CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 0, 6, 0),
                Background = preview ?? Brushes.Transparent, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1),
            };
            var inkItem = new MenuItem
            {
                Header = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children = { swatch, new TextBlock { Text = name == "auto" ? "Automatic" : char.ToUpper(name[0]) + name[1..] } },
                },
                IsChecked = doc.Ink == name,
            };
            var chosenInk = name;
            inkItem.Click += (_, _) =>
            {
                doc.Ink = chosenInk;
                doc.ApplyColor();
                SaveSession();
            };
            inkMenu.Items.Add(inkItem);
        }

        menu.Items.Add(inkMenu);

        var bright = new MenuItem { Header = "Bright colors (all tabs)", IsCheckable = true, IsChecked = _bright };
        bright.Click += (_, _) =>
        {
            _bright = bright.IsChecked;
            foreach (var other in _docs)
            {
                other.ApplyColor();
            }

            SaveSession();
        };
        menu.Items.Add(bright);
        menu.Items.Add(new Separator());
        var close = new MenuItem { Header = "Close tab" };
        close.Click += (_, _) => CloseDoc(doc);
        menu.Items.Add(close);
        return menu;
    }

    private void SaveSession()
    {
        if (_restoring || _disposed)
        {
            return;
        }

        _library.Save(new NoteSession
        {
            Tabs = _docs.Select(d => new NoteTab(d.Path, d.Color, d.Ink)).ToList(),
            Bright = _bright,
            Closed = _closed.Take(500).ToList(),
            Active = Math.Max(0, _tabs.SelectedIndex),
        });
    }

    private void Tell(string text) => _message.Text = text;

    /// <summary>The folder this editor shows.</summary>
    public string Folder => _library.Folder;

    /// <summary>
    /// Makes <paramref name="folder"/> this editor's notes folder: the notes that live in the current folder are moved
    /// (or copied) there and the tabs follow them. Notes opened from somewhere else stay where they are.
    /// </summary>
    public void MoveTo(string folder, bool move)
    {
        if (string.Equals(System.IO.Path.GetFullPath(folder), System.IO.Path.GetFullPath(_library.Folder), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(folder);
            var from = System.IO.Path.GetFullPath(_library.Folder).TrimEnd('\\', '/');
            foreach (var doc in _docs)
            {
                doc.Save();
                var home = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(doc.Path))?.TrimEnd('\\', '/');
                if (!string.Equals(home, from, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var target = NoteLibrary.UniquePath(folder, System.IO.Path.GetFileName(doc.Path));
                if (move)
                {
                    File.Move(doc.Path, target);
                }
                else
                {
                    File.Copy(doc.Path, target);
                }

                doc.Rehome(target);
            }

            _library = new NoteLibrary(folder);
            _closed.Clear();
            SaveSession();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Tell("Could not move the notes: " + ex.Message);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        foreach (var doc in _docs)
        {
            doc.Save();
            doc.Dispose();
        }

        SaveSession();
        _disposed = true;
    }

    private static SolidColorBrush Solid(string hex)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        brush.Freeze();
        return brush;
    }

    private static Brush Resource(string key, Brush fallback) =>
        Application.Current.TryFindResource(key) as Brush ?? fallback;

    private static bool IsDarkTheme()
    {
        if (Application.Current.TryFindResource("Back") is SolidColorBrush back)
        {
            var c = back.Color;
            return (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) < 128;
        }

        return true;
    }

    /// <summary>One open file: its tab, its editor and its autosave.</summary>
    private sealed class NoteDoc : IDisposable
    {
        private readonly NotesView _owner;
        private readonly TextBlock _title = new() { VerticalAlignment = VerticalAlignment.Center, MaxWidth = 160, TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly TextBlock _dirty = new() { Text = "●", Margin = new Thickness(6, 0, 0, 0), FontSize = 9, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
        private readonly Border _header = new() { Padding = new Thickness(10, 4, 4, 4), CornerRadius = new CornerRadius(4, 4, 0, 0) };
        private readonly DispatcherTimer _autosave = new() { Interval = TimeSpan.FromMilliseconds(1500) };
        private readonly Button _closeButton = new()
        {
            Padding = new Thickness(5, 0, 5, 0), Margin = new Thickness(8, 0, 0, 0),
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = Cursors.Hand,
        };
        private bool _loading;

        public NoteDoc(NotesView owner, string path, string color)
        {
            _owner = owner;
            Path = path;
            Color = color;
            Editor = new TextEditor
            {
                ShowLineNumbers = true,
                WordWrap = true,
                FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                FontSize = 14,
                Padding = new Thickness(8, 4, 4, 4),
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            };

            try
            {
                _loading = true;
                Editor.Text = File.ReadAllText(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                owner.Tell($"Could not read {System.IO.Path.GetFileName(path)}: {ex.Message}");
                Failed = true;
                return;
            }
            finally
            {
                _loading = false;
            }

            var close = _closeButton;
            close.Content = "\u00D7";
            close.Click += (_, e) =>
            {
                e.Handled = true;
                _owner.CloseDoc(this);
            };
            _header.Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { _title, _dirty, close } };
            Item = new TabItem { Header = _header, Content = Editor, Padding = new Thickness(0) };
            _header.ContextMenu = owner.ColorMenu(this);
            _header.ContextMenuOpening += (_, _) => _header.ContextMenu = owner.ColorMenu(this);

            Editor.TextChanged += (_, _) =>
            {
                if (_loading)
                {
                    return;
                }

                _dirty.Visibility = Visibility.Visible;
                _autosave.Stop();
                _autosave.Start();
            };
            _autosave.Tick += (_, _) => Save();
            Describe();
        }

        public string Path { get; private set; }

        public string Color { get; set; }

        public string Ink { get; set; } = "auto";

        public bool Failed { get; }

        public TextEditor Editor { get; }

        public TabItem Item { get; } = new();

        public void ApplyColor()
        {
            var dark = _owner._dark;
            var fill = Solid(NoteColors.Background(Color, _owner._bright, dark));
            var light = NoteColors.IsLightBackground(_owner._bright, dark);
            var themeText = Resource("Text", Brushes.White) is SolidColorBrush { Color: var tc } ? $"#{tc.R:X2}{tc.G:X2}{tc.B:X2}" : "#ECEFF4";
            var ink = Solid(NoteColors.InkHex(Ink, light, themeText));
            _header.Background = fill;
            _title.Foreground = ink;
            _dirty.Foreground = ink;
            _closeButton.Foreground = ink;

            Editor.Background = fill;
            Editor.Foreground = ink;
            Editor.LineNumbersForeground = ink;
        }

        public void Save()
        {
            _autosave.Stop();
            if (_dirty.Visibility != Visibility.Visible)
            {
                return;
            }

            try
            {
                File.WriteAllText(Path, Editor.Text, new UTF8Encoding(false));
                _dirty.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _owner.Tell($"Could not save {System.IO.Path.GetFileName(Path)}: {ex.Message}");
            }
        }

        /// <summary>Points the tab at a file that was just moved or copied here (nothing is written).</summary>
        public void Rehome(string path)
        {
            Path = path;
            Describe();
        }

        /// <summary>Writes the text to a new path and carries on editing that file.</summary>
        public void Retarget(string path)
        {
            try
            {
                File.WriteAllText(path, Editor.Text, new UTF8Encoding(false));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _owner.Tell($"Could not save {System.IO.Path.GetFileName(path)}: {ex.Message}");
                return;
            }

            Path = path;
            _dirty.Visibility = Visibility.Collapsed;
            Describe();
        }

        private void Describe()
        {
            _title.Text = System.IO.Path.GetFileName(Path);
            _title.ToolTip = Path;
            var extension = System.IO.Path.GetExtension(Path);
            Editor.SyntaxHighlighting = extension.Length > 0 ? HighlightingManager.Instance.GetDefinitionByExtension(extension) : null;
        }

        public void Dispose() => _autosave.Stop();
    }
}
