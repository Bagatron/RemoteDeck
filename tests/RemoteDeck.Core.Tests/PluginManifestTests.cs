using RemoteDeck.Core.Plugins;

namespace RemoteDeck.Core.Tests;

public class PluginManifestTests
{
    private const string Valid = """
        {
          "id": "acme.proxmox",
          "name": "Proxmox console",
          "version": "1.2.0",
          "minHostApiVersion": "1.0.0",
          "entryPoint": "Acme.Proxmox.dll",
          "author": "Acme",
          "permissions": ["network", "useCredentials"]
        }
        """;

    private const string NoPermissions = """
        {
          "id": "acme.proxmox",
          "name": "Proxmox console",
          "version": "1.2.0",
          "minHostApiVersion": "1.0.0",
          "entryPoint": "Acme.Proxmox.dll"
        }
        """;

    [Fact]
    public void Parse_ReadsAValidManifest()
    {
        var manifest = PluginManifestLoader.Parse(Valid);

        Assert.Equal("acme.proxmox", manifest.Id);
        Assert.Equal("Acme.Proxmox.dll", manifest.EntryPoint);
        Assert.Equal(
            new[] { PluginPermission.Network, PluginPermission.UseCredentials },
            manifest.Permissions);
    }

    [Fact]
    public void Parse_AllowsNoPermissions()
    {
        var manifest = PluginManifestLoader.Parse(NoPermissions);

        Assert.Null(manifest.Permissions);
    }

    [Theory]
    [InlineData("id", "Bad Id")]
    [InlineData("id", "x")]
    [InlineData("version", "one.two")]
    [InlineData("minHostApiVersion", "1.0")]
    [InlineData("entryPoint", "../evil.dll")]
    [InlineData("entryPoint", "sub/plugin.dll")]
    [InlineData("entryPoint", "plugin.exe")]
    public void Parse_RejectsInvalidFields(string field, string badValue)
    {
        var json = Valid.Replace(
            Existing(field),
            $"\"{field}\": \"{badValue}\"",
            StringComparison.Ordinal);

        Assert.Throws<PluginManifestException>(() => PluginManifestLoader.Parse(json));
    }

    [Fact]
    public void Parse_RejectsMissingRequiredFields()
    {
        const string json = """{ "id": "acme.proxmox", "name": "Proxmox" }""";

        Assert.Throws<PluginManifestException>(() => PluginManifestLoader.Parse(json));
    }

    [Fact]
    public void Parse_RejectsUnknownPermissions()
    {
        var json = Valid.Replace("\"network\"", "\"rootAccess\"", StringComparison.Ordinal);

        Assert.Throws<PluginManifestException>(() => PluginManifestLoader.Parse(json));
    }

    [Fact]
    public void Parse_RejectsDuplicatePermissions()
    {
        var json = Valid.Replace("\"useCredentials\"", "\"network\"", StringComparison.Ordinal);

        Assert.Throws<PluginManifestException>(() => PluginManifestLoader.Parse(json));
    }

    [Fact]
    public void Parse_RejectsBrokenJson()
    {
        Assert.Throws<PluginManifestException>(() => PluginManifestLoader.Parse("{ nope"));
    }

    [Theory]
    [InlineData("1.0.0", "1.0.0", true)]
    [InlineData("1.0.0", "1.4.2", true)]
    [InlineData("1.3.0", "1.2.0", false)]
    [InlineData("2.0.0", "1.9.0", false)]
    [InlineData("1.0.0", "2.0.0", false)]
    public void IsCompatible_RequiresSameMajorAndNewEnoughHost(string minimum, string host, bool expected)
    {
        var manifest = PluginManifestLoader.Parse(Valid.Replace("\"1.0.0\"", $"\"{minimum}\"", StringComparison.Ordinal));

        Assert.Equal(expected, PluginManifestLoader.IsCompatible(manifest, Version.Parse(host)));
    }

    [Fact]
    public void SamplePluginManifest_IsValid()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "samples", "HelloPlugin", "plugin.json");

        var manifest = PluginManifestLoader.Parse(File.ReadAllText(path));

        Assert.Equal("sample.hello", manifest.Id);
    }

    // Finds the exact `"field": "value"` text in the valid manifest so a test can swap the value.
    private static string Existing(string field)
    {
        var start = Valid.IndexOf($"\"{field}\":", StringComparison.Ordinal);
        var end = Valid.IndexOf(',', start);
        return Valid[start..end];
    }
}
