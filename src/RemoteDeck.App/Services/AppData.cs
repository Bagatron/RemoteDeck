using System.IO;
using System.Windows;
using RemoteDeck.App.Dialogs;
using RemoteDeck.Core.Connections;
using RemoteDeck.Vault;

namespace RemoteDeck.App.Services;

/// <summary>Where RemoteDeck keeps its files: <c>%LOCALAPPDATA%\RemoteDeck</c>.</summary>
internal static class AppPaths
{
    public static string Folder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RemoteDeck");

    public static string Vault => Path.Combine(Folder, "vault.json");

    public static string Connections => Path.Combine(Folder, "connections.json");

    public static string Settings => Path.Combine(Folder, "settings.json");

    public static string Workspaces => Path.Combine(Folder, "workspaces");

    public static string Plugins => Path.Combine(Folder, "plugins");

    public static string Logs => Path.Combine(Folder, "logs");

    public static string PluginData => Path.Combine(Folder, "plugin-data");

    public static string UserThemes => Path.Combine(Folder, "themes");

    public static string KnownHosts => Path.Combine(Folder, "known_hosts.json");
}

/// <summary>The open vault and the saved connections. Both save themselves whenever they change.</summary>
internal sealed class AppData
{
    private AppData(CredentialVault vault, ConnectionStore store)
    {
        Vault = vault;
        Store = store;

        vault.Changed += (_, _) => SaveVault();
        store.Changed += (_, _) => SaveConnections();
    }

    public CredentialVault Vault { get; }

    public ConnectionStore Store { get; }

    /// <summary>Asks for the master password (creating the vault the first time). Returns null if the user gives up.</summary>
    public static AppData? OpenInteractively()
    {
        var exists = File.Exists(AppPaths.Vault);

        while (true)
        {
            var dialog = new UnlockDialog(creating: !exists);
            if (dialog.ShowDialog() != true)
            {
                return null;
            }

            try
            {
                CredentialVault vault;
                if (exists)
                {
                    vault = VaultStorage.Load(AppPaths.Vault, dialog.Password);
                }
                else
                {
                    vault = CredentialVault.Create(dialog.Password);
                    VaultStorage.Save(vault, AppPaths.Vault);
                }

                var store = CatalogStorage.LoadOrCreate(AppPaths.Connections);
                return new AppData(vault, store);
            }
            catch (InvalidMasterPasswordException)
            {
                MessageBox.Show("That is not the master password.", "RemoteDeck", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex) when (ex is VaultException or CatalogException or IOException)
            {
                MessageBox.Show(
                    $"RemoteDeck could not open its data in\n{AppPaths.Folder}\n\n{ex.Message}",
                    "RemoteDeck",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return null;
            }
        }
    }

    private void SaveVault()
    {
        try
        {
            VaultStorage.Save(Vault, AppPaths.Vault);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show($"Could not save the vault:\n{ex.Message}", "RemoteDeck", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SaveConnections()
    {
        try
        {
            CatalogStorage.Save(Store, AppPaths.Connections);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show($"Could not save your connections:\n{ex.Message}", "RemoteDeck", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
