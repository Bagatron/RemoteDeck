using System.Reflection;
using System.Runtime.Loader;
using RemoteDeck.Plugin;

namespace RemoteDeck.Core.Plugins;

/// <summary>A plugin folder found on disk: either a valid manifest, or the reason it is unusable.</summary>
public sealed record PluginCandidate(string Directory, PluginManifest? Manifest, string? Error);

public sealed class PluginLoadException : Exception
{
    public PluginLoadException(string message)
        : base(message)
    {
    }

    public PluginLoadException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Loads each plugin into its own collectible load context so its private dependencies cannot clash with the
/// host's or another plugin's, and so it can be unloaded. The contracts assembly is always the host's copy.
/// </summary>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private const string ContractsAssembly = "RemoteDeck.Plugin.Abstractions";

    private readonly AssemblyDependencyResolver _resolver;
    private readonly string _directory;

    public PluginLoadContext(string entryAssemblyPath)
        : base("plugin:" + Path.GetFileName(entryAssemblyPath), isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(entryAssemblyPath);
        _directory = Path.GetDirectoryName(entryAssemblyPath)!;
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // Returning null hands the request to the host, so IPlugin means the same type on both sides.
        if (assemblyName.Name is null || assemblyName.Name == ContractsAssembly)
        {
            return null;
        }

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        if (path is null)
        {
            var beside = Path.Combine(_directory, assemblyName.Name + ".dll");
            path = File.Exists(beside) ? beside : null;
        }

        return path is null ? null : LoadFromAssemblyPath(path);
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? 0 : LoadUnmanagedDllFromPath(path);
    }
}

/// <summary>A plugin that is running.</summary>
public sealed class LoadedPlugin : IDisposable
{
    private readonly IPlugin _plugin;
    private readonly PluginLoadContext _context;
    private readonly Action<string, string, Exception?> _warn;
    private bool _disposed;

    internal LoadedPlugin(
        PluginManifest manifest,
        IReadOnlySet<PluginPermission> granted,
        IPlugin plugin,
        PluginLoadContext context,
        IReadOnlyList<PluginCommand> commands,
        IReadOnlyList<IConnectionFactory> connectionTypes,
        Action<string, string, Exception?> warn)
    {
        Manifest = manifest;
        Granted = granted;
        _plugin = plugin;
        _context = context;
        Commands = commands;
        ConnectionTypes = connectionTypes;
        _warn = warn;
    }

    public PluginManifest Manifest { get; }

    public IReadOnlySet<PluginPermission> Granted { get; }

    public IReadOnlyList<PluginCommand> Commands { get; }

    public IReadOnlyList<IConnectionFactory> ConnectionTypes { get; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _plugin.Dispose();
        }
        catch (Exception ex)
        {
            _warn(Manifest.Id, "The plugin threw while shutting down.", ex);
        }

        _context.Unload();
    }
}

/// <summary>Finds and runs plugins. Which ones to run, and with which permissions, is the caller's decision.</summary>
public sealed class PluginHost : IDisposable
{
    /// <summary>The contracts version this host implements.</summary>
    public static Version CurrentApiVersion { get; } = new(0, 1, 0);

    private static readonly HashSet<string> ReservedTypes = new(StringComparer.OrdinalIgnoreCase) { "ssh", "rdp", "web", "telnet", "serial" };

    private readonly string _dataRoot;
    private readonly ICredentialBroker _credentials;
    private readonly Action<string, PluginLogLevel, string, Exception?>? _log;
    private readonly Dictionary<string, LoadedPlugin> _loaded = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _typeOwners = new(StringComparer.OrdinalIgnoreCase);

    public PluginHost(
        string dataRoot,
        ICredentialBroker credentials,
        Action<string, PluginLogLevel, string, Exception?>? log = null)
    {
        _dataRoot = dataRoot ?? throw new ArgumentNullException(nameof(dataRoot));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _log = log;
    }

    public IReadOnlyCollection<LoadedPlugin> Loaded => _loaded.Values.ToArray();

    public IEnumerable<PluginCommand> Commands => _loaded.Values.SelectMany(p => p.Commands);

    public IEnumerable<IConnectionFactory> ConnectionTypes => _loaded.Values.SelectMany(p => p.ConnectionTypes);

    public bool IsLoaded(string pluginId) => _loaded.ContainsKey(pluginId);

    /// <summary>Looks one folder level down in each root for a plugin.json. A folder that cannot be read is listed with its error.</summary>
    public static IReadOnlyList<PluginCandidate> Discover(IEnumerable<string> roots)
    {
        var found = new List<PluginCandidate>();
        foreach (var root in roots)
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var directory in Directory.EnumerateDirectories(root).Order(StringComparer.OrdinalIgnoreCase))
            {
                var manifestPath = Path.Combine(directory, "plugin.json");
                if (!File.Exists(manifestPath))
                {
                    continue;
                }

                try
                {
                    found.Add(new PluginCandidate(directory, PluginManifestLoader.Parse(File.ReadAllText(manifestPath)), null));
                }
                catch (Exception ex) when (ex is PluginManifestException or IOException or UnauthorizedAccessException)
                {
                    found.Add(new PluginCandidate(directory, null, ex.Message));
                }
            }
        }

        return found;
    }

    /// <summary>Whether this host can run the plugin, and if not, why.</summary>
    public static string? IncompatibilityReason(PluginManifest manifest) =>
        PluginManifestLoader.IsCompatible(manifest, CurrentApiVersion)
            ? null
            : $"It needs plugin API {manifest.MinHostApiVersion} but this RemoteDeck provides {CurrentApiVersion}.";

    /// <summary>Loads and starts a plugin with exactly the permissions in <paramref name="granted"/>.</summary>
    public LoadedPlugin Load(PluginCandidate candidate, IReadOnlySet<PluginPermission> granted)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(granted);

        var manifest = candidate.Manifest ?? throw new PluginLoadException(candidate.Error ?? "Invalid plugin.");
        if (_loaded.ContainsKey(manifest.Id))
        {
            throw new PluginLoadException($"Plugin '{manifest.Id}' is already loaded.");
        }

        if (IncompatibilityReason(manifest) is { } reason)
        {
            throw new PluginLoadException(reason);
        }

        var directory = Path.GetFullPath(candidate.Directory);
        var entry = Path.GetFullPath(Path.Combine(directory, manifest.EntryPoint));
        if (!File.Exists(entry))
        {
            throw new PluginLoadException($"The plugin file '{manifest.EntryPoint}' was not found in {directory}.");
        }

        var context = new PluginLoadContext(entry);
        var commands = new List<PluginCommand>();
        var types = new List<IConnectionFactory>();
        IPlugin? plugin = null;
        var registered = new List<string>();
        try
        {
            var assembly = context.LoadFromAssemblyPath(entry);
            plugin = CreateInstance(assembly, manifest.Id);

            var dataDirectory = Path.Combine(_dataRoot, manifest.Id);
            Directory.CreateDirectory(dataDirectory);

            plugin.Initialize(new PluginContext(
                directory,
                dataDirectory,
                new CommandRegistry(commands),
                new TypeRegistry(types, registered, manifest.Id, _typeOwners),
                new GatedBroker(_credentials, granted.Contains(PluginPermission.UseCredentials), manifest.Id),
                new PluginLogger(manifest.Id, _log)));
        }
        catch (Exception ex)
        {
            foreach (var type in registered)
            {
                _typeOwners.Remove(type);
            }

            try
            {
                plugin?.Dispose();
            }
            catch (Exception)
            {
                // Already failing; the original error is the useful one.
            }

            context.Unload();
            throw ex as PluginLoadException ?? new PluginLoadException($"'{manifest.Name}' could not start: {ex.Message}", ex);
        }

        var loaded = new LoadedPlugin(
            manifest,
            granted,
            plugin!,
            context,
            commands,
            types,
            (id, message, error) => _log?.Invoke(id, PluginLogLevel.Warning, message, error));
        _loaded[manifest.Id] = loaded;
        return loaded;
    }

    public bool Unload(string pluginId)
    {
        if (!_loaded.Remove(pluginId, out var plugin))
        {
            return false;
        }

        foreach (var type in plugin.ConnectionTypes)
        {
            _typeOwners.Remove(type.Type);
        }

        plugin.Dispose();
        return true;
    }

    public void Dispose()
    {
        foreach (var id in _loaded.Keys.ToArray())
        {
            Unload(id);
        }
    }

    private static IPlugin CreateInstance(Assembly assembly, string pluginId)
    {
        Type[] all;
        try
        {
            all = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            all = ex.Types.Where(t => t is not null).Select(t => t!).ToArray();
        }

        var candidates = all.Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IPlugin).IsAssignableFrom(t)).ToList();
        if (candidates.Count != 1)
        {
            throw new PluginLoadException(
                $"Plugin '{pluginId}' must contain exactly one class that implements IPlugin (found {candidates.Count}).");
        }

        return (IPlugin?)Activator.CreateInstance(candidates[0])
            ?? throw new PluginLoadException($"Could not create the plugin class {candidates[0].Name}.");
    }

    private sealed class PluginContext : IPluginContext
    {
        public PluginContext(
            string directory,
            string dataDirectory,
            ICommandRegistry commands,
            IConnectionTypeRegistry types,
            ICredentialBroker credentials,
            IPluginLogger log)
        {
            PluginDirectory = directory;
            DataDirectory = dataDirectory;
            Commands = commands;
            ConnectionTypes = types;
            Credentials = credentials;
            Log = log;
        }

        public Version HostApiVersion => CurrentApiVersion;

        public string PluginDirectory { get; }

        public string DataDirectory { get; }

        public ICommandRegistry Commands { get; }

        public IConnectionTypeRegistry ConnectionTypes { get; }

        public ICredentialBroker Credentials { get; }

        public IPluginLogger Log { get; }
    }

    private sealed class CommandRegistry : ICommandRegistry
    {
        private readonly List<PluginCommand> _commands;

        public CommandRegistry(List<PluginCommand> commands)
        {
            _commands = commands;
        }

        public void Register(PluginCommand command)
        {
            ArgumentNullException.ThrowIfNull(command);
            if (string.IsNullOrWhiteSpace(command.Id) || string.IsNullOrWhiteSpace(command.Title))
            {
                throw new PluginLoadException("A command needs an id and a title.");
            }

            if (_commands.Any(c => c.Id == command.Id))
            {
                throw new PluginLoadException($"The command id '{command.Id}' is registered twice.");
            }

            _commands.Add(command);
        }
    }

    private sealed class TypeRegistry : IConnectionTypeRegistry
    {
        private readonly List<IConnectionFactory> _factories;
        private readonly List<string> _registered;
        private readonly string _pluginId;
        private readonly Dictionary<string, string> _owners;

        public TypeRegistry(List<IConnectionFactory> factories, List<string> registered, string pluginId, Dictionary<string, string> owners)
        {
            _factories = factories;
            _registered = registered;
            _pluginId = pluginId;
            _owners = owners;
        }

        public void Register(IConnectionFactory factory)
        {
            ArgumentNullException.ThrowIfNull(factory);
            var type = factory.Type?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(type))
            {
                throw new PluginLoadException("A connection type needs an id.");
            }

            if (ReservedTypes.Contains(type))
            {
                throw new PluginLoadException($"The connection type '{type}' is built in and cannot be replaced by a plugin.");
            }

            if (_owners.TryGetValue(type, out var owner))
            {
                throw new PluginLoadException($"The connection type '{type}' is already provided by '{owner}'.");
            }

            _owners[type] = _pluginId;
            _registered.Add(type);
            _factories.Add(factory);
        }
    }

    private sealed class PluginLogger : IPluginLogger
    {
        private readonly string _pluginId;
        private readonly Action<string, PluginLogLevel, string, Exception?>? _sink;

        public PluginLogger(string pluginId, Action<string, PluginLogLevel, string, Exception?>? sink)
        {
            _pluginId = pluginId;
            _sink = sink;
        }

        public void Log(PluginLogLevel level, string message, Exception? exception = null) =>
            _sink?.Invoke(_pluginId, level, message, exception);
    }

    /// <summary>Hands credentials to a plugin only when the user granted it the useCredentials permission.</summary>
    private sealed class GatedBroker : ICredentialBroker
    {
        private readonly ICredentialBroker _inner;
        private readonly bool _allowed;
        private readonly string _pluginId;

        public GatedBroker(ICredentialBroker inner, bool allowed, string pluginId)
        {
            _inner = inner;
            _allowed = allowed;
            _pluginId = pluginId;
        }

        public ValueTask<T> UseAsync<T>(
            string connectionId,
            Func<ICredential, ValueTask<T>> use,
            CancellationToken cancellationToken = default)
        {
            if (!_allowed)
            {
                throw new UnauthorizedAccessException(
                    $"Plugin '{_pluginId}' was not given the useCredentials permission.");
            }

            return _inner.UseAsync(connectionId, use, cancellationToken);
        }
    }
}
