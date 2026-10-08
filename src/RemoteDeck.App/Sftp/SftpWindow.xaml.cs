using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using RemoteDeck.App.Dialogs;
using RemoteDeck.Protocols.Ssh;

namespace RemoteDeck.App.Sftp;

/// <summary>One row of the file list.</summary>
internal sealed class FileRow
{
    public FileRow(SftpEntry entry)
    {
        Entry = entry;
    }

    public SftpEntry Entry { get; }

    public string Display => (Entry.IsDirectory ? "\U0001F4C1  " : "\U0001F4C4  ") + Entry.Name;

    public string SizeText => Entry.IsDirectory ? string.Empty : FormatSize(Entry.Size);

    public string ModifiedText => Entry.Modified == DateTime.MinValue
        ? string.Empty
        : Entry.Modified.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);

    public static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{value:0.#} {units[unit]}";
    }
}

/// <summary>Browse a server's files and move them to and from this PC. Drop files from Explorer to upload.</summary>
public partial class SftpWindow : Window
{
    private readonly SftpSession _session;
    private string _path = "/";
    private CancellationTokenSource? _cancel;
    private bool _busy;

    internal SftpWindow(Window owner, string title, SftpSession session)
    {
        InitializeComponent();
        Owner = owner;
        Title = "SFTP - " + title;
        _session = session;

        Loaded += async (_, _) => await NavigateAsync(_session.HomeDirectory);
        Closed += (_, _) =>
        {
            _cancel?.Cancel();
            _session.Dispose();
        };
    }

    private IEnumerable<SftpEntry> Selected => Files.SelectedItems.OfType<FileRow>().Select(r => r.Entry);

    private void Say(string text) => StatusText.Text = text;

    private async Task NavigateAsync(string path)
    {
        try
        {
            var entries = await _session.ListAsync(path);
            _path = path;
            PathBox.Text = path;
            Files.ItemsSource = entries.Select(e => new FileRow(e)).ToList();
            Say($"{entries.Count} item(s)");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PathBox.Text = _path;
            Say("Could not open " + path + ": " + ex.Message);
        }
    }

    private Task RefreshAsync() => NavigateAsync(_path);

    private async void Up_Click(object sender, RoutedEventArgs e) => await NavigateAsync(RemotePath.Parent(_path));

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void PathBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && PathBox.Text.Trim().Length > 0)
        {
            await NavigateAsync(PathBox.Text.Trim());
        }
    }

    private async void Files_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Files.SelectedItem is FileRow { Entry.IsDirectory: true } row)
        {
            await NavigateAsync(row.Entry.FullPath);
        }
    }

    // ---- upload ----

    private async void UploadFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Upload files", Multiselect = true };
        if (dialog.ShowDialog(this) == true)
        {
            await UploadAsync(dialog.FileNames);
        }
    }

    private async void UploadFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Upload a folder" };
        if (dialog.ShowDialog(this) == true)
        {
            await UploadAsync(new[] { dialog.FolderName });
        }
    }

    private void Files_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Files_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0)
        {
            await UploadAsync(paths);
        }
    }

    private async Task UploadAsync(IReadOnlyList<string> paths)
    {
        var existing = Files.Items.OfType<FileRow>().Select(r => r.Entry.Name).ToHashSet(StringComparer.Ordinal);
        var clashes = paths
            .Select(p => Path.GetFileName(p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))
            .Where(existing.Contains)
            .ToList();
        if (clashes.Count > 0 && !Confirm($"These already exist here and will be replaced:\n\n{string.Join("\n", clashes.Take(10))}\n\nContinue?"))
        {
            return;
        }

        var target = _path;
        await RunAsync($"Uploading {paths.Count} item(s)", async (progress, token) =>
        {
            foreach (var path in paths)
            {
                await _session.UploadAsync(path, target, progress, token);
            }
        });
        await RefreshAsync();
    }

    // ---- download ----

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        var entries = Selected.ToList();
        if (entries.Count == 0)
        {
            Say("Select what to download first.");
            return;
        }

        var dialog = new OpenFolderDialog { Title = "Download into which folder?" };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var folder = dialog.FolderName;
        var clashes = entries.Where(en => File.Exists(Path.Combine(folder, en.Name)) || Directory.Exists(Path.Combine(folder, en.Name))).Select(en => en.Name).ToList();
        if (clashes.Count > 0 && !Confirm($"These already exist in that folder and will be replaced:\n\n{string.Join("\n", clashes.Take(10))}\n\nContinue?"))
        {
            return;
        }

        await RunAsync($"Downloading {entries.Count} item(s)", async (progress, token) =>
        {
            foreach (var entry in entries)
            {
                await _session.DownloadAsync(entry, folder, progress, token);
            }
        });
    }

    // ---- change things on the server ----

    private async void NewFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new NameDialog("New folder", "Folder name") { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            await RunAsync("Creating folder", (_, _) => _session.CreateDirectoryAsync(RemotePath.Combine(_path, dialog.Value)));
            await RefreshAsync();
        }
    }

    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (Files.SelectedItems.Count != 1 || Files.SelectedItem is not FileRow row)
        {
            Say("Select one item to rename.");
            return;
        }

        var dialog = new NameDialog("Rename", "New name", row.Entry.Name) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Value != row.Entry.Name)
        {
            var target = RemotePath.Combine(_path, dialog.Value);
            await RunAsync("Renaming", (_, _) => _session.RenameAsync(row.Entry.FullPath, target));
            await RefreshAsync();
        }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        var entries = Selected.ToList();
        if (entries.Count == 0)
        {
            Say("Select what to delete first.");
            return;
        }

        var what = entries.Count == 1 ? $"\"{entries[0].Name}\"" : $"{entries.Count} items";
        if (!Confirm($"Delete {what} from the server? Folders are deleted with everything inside. This cannot be undone."))
        {
            return;
        }

        await RunAsync("Deleting", async (_, _) =>
        {
            foreach (var entry in entries)
            {
                await _session.DeleteAsync(entry);
            }
        });
        await RefreshAsync();
    }

    // ---- plumbing ----

    private async Task RunAsync(string label, Func<IProgress<long>, CancellationToken, Task> work)
    {
        if (_busy)
        {
            Say("Wait for the current transfer to finish, or cancel it.");
            return;
        }

        _busy = true;
        _cancel = new CancellationTokenSource();
        Busy.Visibility = Visibility.Visible;
        CancelButton.Visibility = Visibility.Visible;
        long total = 0;
        Say(label + "...");

        // Progress<T> created here reports back on the UI thread.
        var progress = new Progress<long>(n =>
        {
            total += n;
            Say($"{label}... {FileRow.FormatSize(total)}");
        });

        try
        {
            await work(progress, _cancel.Token);
            Say(total > 0 ? $"{label} finished ({FileRow.FormatSize(total)})." : label + " finished.");
        }
        catch (OperationCanceledException)
        {
            Say(label + " cancelled.");
        }
        catch (Exception ex)
        {
            Say(label + " failed: " + ex.Message);
        }
        finally
        {
            _busy = false;
            _cancel.Dispose();
            _cancel = null;
            Busy.Visibility = Visibility.Collapsed;
            CancelButton.Visibility = Visibility.Collapsed;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _cancel?.Cancel();

    private bool Confirm(string text) =>
        MessageBox.Show(this, text, "SFTP", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
}
