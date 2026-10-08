using System.IO;
using RemoteDeck.Core.Plugins;
using RemoteDeck.Plugin;

namespace RemoteDeck.App.Services;

/// <summary>One plugin as the Plugins window shows it.</summary>
internal sealed record PluginRow(PluginCandidate Candidate, string Status, bool Running, bool CanEnable);

/// <summary>
/// Decides which plugins run. A plugin is off until you turn it on and approve the permissions it lists; if a
/// newer version asks for more than you approved, it stays off until you approve again.
/// </summary>
internal sealed class PluginManager : IDisposable
{
    private readonly AppSettings _settings;
    private readonly Dictionary<string, string> _errors = new(StringComparer.Ordinal);

    public PluginManager(AppSettings settings, ICredentialBroker credentials, Action<string>? say = null)
    {
        _settings = settings;
        Host = new PluginHost(
            AppPaths.PluginData,
            credentials,
            (id, level, message, error) => say?.Invoke($"[{id}] {message}" + (error is null ? string.Empty : ": " + error.Message)));
    }

    public PluginHost Host { get; }

    public IReadOnlyList<PluginCandidate> Candidates { get; private set; } = Array.Empty<PluginCandidate>();

    public string UserFolder => AppPaths.Plugins;

    /// <summary>Raised when plugins start or stop, so menus and the connection dialog can refresh.</summary>
    public event Action? Changed;

    public void Start()
    {
        Directory.CreateDirectory(UserFolder);
        Refresh(loadApproved: true);
    }

    public void Refresh(bool loadApproved = false)
    {
        Candidates = PluginHost.Discover(new[] { Path.Combine(AppContext.BaseDirectory, "plugins"), UserFolder });

        if (loadApproved)
        {
            foreach (var candidate in Candidates.Where(c => c.Manifest is not null))
            {
                var manifest = candidate.Manifest!;
                if (!_settings.Plugins.TryGetValue(manifest.Id, out var approved) || Host.IsLoaded(manifest.Id))
                {
                    continue;
                }

                if (!Covers(approved, manifest))
                {
                    _errors[manifest.Id] = "needs approval again: it now asks for more permissions";
                    continue;
                }

                TryLoad(candidate);
            }
        }

        Changed?.Invoke();
    }

    public IReadOnlyList<PluginRow> Rows() =>
        Candidates.Select(c =>
        {
            if (c.Manifest is null)
            {
                return new PluginRow(c, "Invalid: " + c.Error, false, false);
            }

            var id = c.Manifest.Id;
            if (Host.IsLoaded(id))
            {
                return new PluginRow(c, "Running", true, false);
            }

            if (PluginHost.IncompatibilityReason(c.Manifest) is { } reason)
            {
                return new PluginRow(c, "Incompatible. " + reason, false, false);
            }

            return _errors.TryGetValue(id, out var error)
                ? new PluginRow(c, "Off: " + error, false, true)
                : new PluginRow(c, "Off", false, true);
        }).ToList();

    /// <summary>Turns a plugin on with every permission it lists. The caller has already asked the user.</summary>
    public string? Enable(PluginCandidate candidate)
    {
        var manifest = candidate.Manifest!;
        _settings.Plugins[manifest.Id] = (manifest.Permissions ?? Array.Empty<PluginPermission>()).Select(p => p.ToString()).ToList();
        _settings.Save();
        var error = TryLoad(candidate);
        Changed?.Invoke();
        return error;
    }

    public void Disable(PluginCandidate candidate)
    {
        var id = candidate.Manifest!.Id;
        Host.Unload(id);
        _settings.Plugins.Remove(id);
        _settings.Save();
        _errors.Remove(id);
        Changed?.Invoke();
    }

    public void Dispose() => Host.Dispose();

    private string? TryLoad(PluginCandidate candidate)
    {
        var manifest = candidate.Manifest!;
        try
        {
            Host.Load(candidate, (manifest.Permissions ?? Array.Empty<PluginPermission>()).ToHashSet());
            _errors.Remove(manifest.Id);
            return null;
        }
        catch (PluginLoadException ex)
        {
            _errors[manifest.Id] = ex.Message;
            return ex.Message;
        }
    }

    private static bool Covers(IEnumerable<string> approved, PluginManifest manifest)
    {
        var granted = approved.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return (manifest.Permissions ?? Array.Empty<PluginPermission>()).All(p => granted.Contains(p.ToString()));
    }
}
