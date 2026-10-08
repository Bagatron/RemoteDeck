using RemoteDeck.Core.Plugins;
using RemoteDeck.Plugin;

namespace RemoteDeck.Core.Tests;

/// <summary>
/// The one plugin class in this assembly. The tests copy this assembly into a plugin folder and load it the way
/// the app loads a real plugin; a "mode.txt" next to it chooses what it does.
/// </summary>
public sealed class TestPluginEntry : IPlugin
{
    private IPluginContext? _context;

    public void Initialize(IPluginContext context)
    {
        _context = context;
        var modeFile = Path.Combine(context.PluginDirectory, "mode.txt");
        var mode = File.Exists(modeFile) ? File.ReadAllText(modeFile).Trim() : "normal";

        switch (mode)
        {
            case "throw":
                throw new InvalidOperationException("boom");
            case "reserved":
                context.ConnectionTypes.Register(new EchoFactory("SSH"));
                return;
            case "creds":
                try
                {
                    context.Credentials.UseAsync<int>("c1", _ => ValueTask.FromResult(1)).AsTask().GetAwaiter().GetResult();
                    context.Log.Log(PluginLogLevel.Information, "creds:ok");
                }
                catch (UnauthorizedAccessException)
                {
                    context.Log.Log(PluginLogLevel.Information, "creds:denied");
                }

                return;
        }

        context.Commands.Register(new PluginCommand(
            "test.hello",
            "Test: hello",
            _ =>
            {
                context.Log.Log(PluginLogLevel.Information, "hello ran");
                return ValueTask.CompletedTask;
            }));
        context.ConnectionTypes.Register(new EchoFactory("test-echo"));
    }

    public void Dispose() => _context?.Log.Log(PluginLogLevel.Information, "disposed");

    private sealed class EchoFactory : IConnectionFactory
    {
        public EchoFactory(string type)
        {
            Type = type;
        }

        public string Type { get; }

        public string DisplayName => "Echo";

        public IConnection Create(ConnectionDefinition definition, ICredentialBroker credentials) =>
            throw new NotSupportedException();
    }
}

public class PluginHostTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rd-plugins-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _log = new();

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class InnerBroker : ICredentialBroker
    {
        public ValueTask<T> UseAsync<T>(string connectionId, Func<ICredential, ValueTask<T>> use, CancellationToken cancellationToken = default) =>
            use(new Cred());

        private sealed class Cred : ICredential
        {
            public string? Username => "u";

            public void ReadPassword(SecretReader reader) => reader("pw");
        }
    }

    private PluginHost Host() => new(
        Path.Combine(_root, "data"),
        new InnerBroker(),
        (id, level, message, _) => _log.Add($"{id}:{message}"));

    private static string Manifest(string id = "test.plugin", string minApi = "0.1.0") => $$"""
        { "id": "{{id}}", "name": "Test plugin", "version": "1.0.0", "minHostApiVersion": "{{minApi}}", "entryPoint": "TestPlugin.dll" }
        """;

    private string MakePlugin(string folder, string manifest, string? mode = null, bool withDll = true)
    {
        var dir = Path.Combine(_root, "plugins", folder);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "plugin.json"), manifest);
        if (mode is not null)
        {
            File.WriteAllText(Path.Combine(dir, "mode.txt"), mode);
        }

        if (withDll)
        {
            File.Copy(typeof(TestPluginEntry).Assembly.Location, Path.Combine(dir, "TestPlugin.dll"));
        }

        return dir;
    }

    private IReadOnlyList<PluginCandidate> Discover() => PluginHost.Discover(new[] { Path.Combine(_root, "plugins") });

    private static readonly IReadOnlySet<PluginPermission> None = new HashSet<PluginPermission>();

    [Fact]
    public void Discovery_ListsGoodAndBadFolders_AndIgnoresOthers()
    {
        MakePlugin("good", Manifest());
        MakePlugin("bad", "{ nope", withDll: false);
        Directory.CreateDirectory(Path.Combine(_root, "plugins", "not-a-plugin"));

        var found = Discover();

        Assert.Equal(2, found.Count);
        Assert.Equal("test.plugin", found.Single(c => c.Manifest is not null).Manifest!.Id);
        Assert.NotNull(found.Single(c => c.Manifest is null).Error);
    }

    [Fact]
    public void DiscoveryOfAMissingFolder_IsEmpty() =>
        Assert.Empty(PluginHost.Discover(new[] { Path.Combine(_root, "nowhere") }));

    [Fact]
    public void ALoadedPlugin_RegistersItsCommandsAndTypes_AndRunsTheCommand()
    {
        MakePlugin("good", Manifest());
        using var host = Host();

        var loaded = host.Load(Discover().Single(), None);

        Assert.Equal("Test: hello", Assert.Single(loaded.Commands).Title);
        Assert.Equal("test-echo", Assert.Single(host.ConnectionTypes).Type);
        loaded.Commands[0].Execute(CancellationToken.None).AsTask().GetAwaiter().GetResult();
        Assert.Contains("test.plugin:hello ran", _log);
        Assert.True(Directory.Exists(Path.Combine(_root, "data", "test.plugin")));
    }

    [Fact]
    public void Unloading_DisposesThePlugin_AndFreesItsTypeName()
    {
        MakePlugin("good", Manifest());
        using var host = Host();
        host.Load(Discover().Single(), None);

        Assert.True(host.Unload("test.plugin"));

        Assert.Contains("test.plugin:disposed", _log);
        Assert.Empty(host.ConnectionTypes);
        Assert.False(host.Unload("test.plugin"));
        host.Load(Discover().Single(), None);
        Assert.True(host.IsLoaded("test.plugin"));
    }

    [Fact]
    public void LoadingTheSamePluginTwice_IsRefused()
    {
        MakePlugin("good", Manifest());
        using var host = Host();
        host.Load(Discover().Single(), None);

        Assert.Throws<PluginLoadException>(() => host.Load(Discover().Single(), None));
    }

    [Fact]
    public void APluginForANewerApi_IsRefused()
    {
        MakePlugin("future", Manifest(minApi: "9.0.0"));
        using var host = Host();

        var error = Assert.Throws<PluginLoadException>(() => host.Load(Discover().Single(), None));

        Assert.Contains("9.0.0", error.Message);
    }

    [Fact]
    public void AMissingDll_IsReported()
    {
        MakePlugin("nodll", Manifest(), withDll: false);
        using var host = Host();

        var error = Assert.Throws<PluginLoadException>(() => host.Load(Discover().Single(), None));

        Assert.Contains("TestPlugin.dll", error.Message);
    }

    [Fact]
    public void AnInitializeThatThrows_FailsTheLoadCleanly()
    {
        MakePlugin("throws", Manifest(), mode: "throw");
        using var host = Host();

        var error = Assert.Throws<PluginLoadException>(() => host.Load(Discover().Single(), None));

        Assert.Contains("boom", error.Message);
        Assert.False(host.IsLoaded("test.plugin"));
    }

    [Fact]
    public void ABuiltInTypeNameCannotBeTaken()
    {
        MakePlugin("reserved", Manifest(), mode: "reserved");
        using var host = Host();

        var error = Assert.Throws<PluginLoadException>(() => host.Load(Discover().Single(), None));

        Assert.Contains("built in", error.Message);
    }

    [Fact]
    public void TwoPluginsCannotShareAConnectionType()
    {
        MakePlugin("one", Manifest("test.one"));
        MakePlugin("two", Manifest("test.two"));
        using var host = Host();
        var candidates = Discover();

        host.Load(candidates.Single(c => c.Manifest!.Id == "test.one"), None);
        var error = Assert.Throws<PluginLoadException>(() => host.Load(candidates.Single(c => c.Manifest!.Id == "test.two"), None));

        Assert.Contains("already provided", error.Message);
    }

    [Fact]
    public void Credentials_AreOnlyAvailableWithThePermission()
    {
        MakePlugin("creds", Manifest(), mode: "creds");

        using (var denied = Host())
        {
            denied.Load(Discover().Single(), None);
        }

        using (var allowed = Host())
        {
            allowed.Load(Discover().Single(), new HashSet<PluginPermission> { PluginPermission.UseCredentials });
        }

        Assert.Equal(new[] { "test.plugin:creds:denied", "test.plugin:creds:ok" }, _log.Where(l => l.Contains("creds:")).ToArray());
    }
}
