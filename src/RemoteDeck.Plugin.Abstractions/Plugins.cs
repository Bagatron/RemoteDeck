namespace RemoteDeck.Plugin;

public enum PluginLogLevel
{
    Debug,
    Information,
    Warning,
    Error,
}

public interface IPluginLogger
{
    void Log(PluginLogLevel level, string message, Exception? exception = null);
}

/// <summary>A command that shows up in the Ctrl+K palette and can be bound to a key.</summary>
public sealed record PluginCommand(
    string Id,
    string Title,
    Func<CancellationToken, ValueTask> Execute,
    string? DefaultKeybinding = null);

public interface ICommandRegistry
{
    void Register(PluginCommand command);
}

/// <summary>Everything a plugin is allowed to reach. Grows over time, but never breaks within a major version.</summary>
public interface IPluginContext
{
    /// <summary>Version of this contracts API that the host implements.</summary>
    Version HostApiVersion { get; }

    /// <summary>Folder the plugin was loaded from (read-only by convention).</summary>
    string PluginDirectory { get; }

    /// <summary>A private folder for the plugin's own settings and cache.</summary>
    string DataDirectory { get; }

    ICommandRegistry Commands { get; }

    IConnectionTypeRegistry ConnectionTypes { get; }

    ICredentialBroker Credentials { get; }

    IPluginLogger Log { get; }
}

/// <summary>
/// Entry point of a plugin. The host creates exactly one instance per plugin assembly,
/// calls <see cref="Initialize"/> once, and disposes it on unload.
/// </summary>
public interface IPlugin : IDisposable
{
    void Initialize(IPluginContext context);
}
