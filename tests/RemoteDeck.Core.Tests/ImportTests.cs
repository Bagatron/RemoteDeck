using RemoteDeck.Core.Connections;
using RemoteDeck.Core.Import;

namespace RemoteDeck.Core.Tests;

public class ImportTests
{
    [Fact]
    public void SshConfig_ReadsConcreteHosts_AndSkipsWildcards()
    {
        const string text = """
            Host *
                ServerAliveInterval 30

            Host web web-alias *.internal !bad
                HostName 10.0.0.5
                User deploy
                Port 2222
                IdentityFile ~/.ssh/id_ed25519

            Host plain
            """;

        var result = SshConfigImporter.Parse(text, "C:/Users/me");

        Assert.Single(result.Folders);
        Assert.Equal(new[] { "web", "web-alias", "plain" }, result.Connections.Select(c => c.Name));
        var web = result.Connections[0];
        Assert.Equal("10.0.0.5", web.Host);
        Assert.Equal(2222, web.Port);
        Assert.Equal("deploy", web.Options!["username"]);
        Assert.Equal("C:/Users/me/.ssh/id_ed25519", web.Options["privateKeyPath"]);
        Assert.Equal("plain", result.Connections[2].Host);
        Assert.Null(result.Connections[2].Options);
    }

    [Fact]
    public void SshConfig_FirstValueWins_AndProxyJumpIsWarnedAbout()
    {
        var result = SshConfigImporter.Parse("Host a\n  User one\n  User two\n  ProxyJump bastion\n", "/home/me");

        Assert.Equal("one", result.Connections.Single().Options!["username"]);
        Assert.Contains(result.Warnings, w => w.Contains("ProxyJump"));
    }

    [Fact]
    public void SshConfig_ProxyJump_LinksToAnotherHostInTheFile()
    {
        var result = SshConfigImporter.Parse("Host bastion\n  HostName b.example.com\nHost app\n  HostName 10.0.0.5\n  ProxyJump bastion\n", "/home/me");

        var bastion = result.Connections.Single(c => c.Name == "bastion");
        var app = result.Connections.Single(c => c.Name == "app");
        Assert.Equal(bastion.Id, app.Options!["proxyJump"]);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void SshConfig_ProxyJump_CanPointAtALaterHost()
    {
        var result = SshConfigImporter.Parse("Host app\n  ProxyJump bastion\nHost bastion\n", "/home/me");

        Assert.Equal(result.Connections.Single(c => c.Name == "bastion").Id, result.Connections.Single(c => c.Name == "app").Options!["proxyJump"]);
    }

    [Fact]
    public void SshConfig_ProxyJump_ToAChainOrUnknownHost_WarnsAndConnectsDirectly()
    {
        var result = SshConfigImporter.Parse("Host a\n  ProxyJump x,y\nHost b\n  ProxyJump me@nowhere:2200\nHost c\n  ProxyJump c\n", "/home/me");

        Assert.All(result.Connections, c => Assert.Null(c.Options));
        Assert.Equal(3, result.Warnings.Count);
        Assert.All(result.Warnings, w => Assert.Contains("ProxyJump", w));
    }

    [Fact]
    public void SshConfig_PortForwards_AreConverted_AndRepeatsAreKept()
    {
        var text = "Host db\n  LocalForward 8080 internal:80\n  LocalForward 127.0.0.1:8443 internal:443\n  RemoteForward 9000 localhost:3000\n  DynamicForward 1080\n";

        var result = SshConfigImporter.Parse(text, "/home/me");

        Assert.Equal(
            "L:8080:internal:80\nL:8443:internal:443\nR:9000:localhost:3000\nD:1080",
            result.Connections.Single().Options!["forwards"]);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void SshConfig_UnreadableForward_IsSkippedWithAWarning()
    {
        var result = SshConfigImporter.Parse("Host db\n  LocalForward nonsense\n  LocalForward 8080 h:80\n", "/home/me");

        Assert.Equal("L:8080:h:80", result.Connections.Single().Options!["forwards"]);
        Assert.Single(result.Warnings);
    }

    [Fact]
    public void SshConfig_WithNoHosts_ImportsNothing()
    {
        var result = SshConfigImporter.Parse("# nothing here\n", "/home/me");

        Assert.Empty(result.Folders);
        Assert.Empty(result.Connections);
    }

    [Fact]
    public void Putty_ReadsSshSessions_AndDecodesNames()
    {
        const string text = "Windows Registry Editor Version 5.00\r\n\r\n"
            + "[HKEY_CURRENT_USER\\Software\\SimonTatham\\PuTTY\\Sessions\\Default%20Settings]\r\n\"HostName\"=\"\"\r\n\r\n"
            + "[HKEY_CURRENT_USER\\Software\\SimonTatham\\PuTTY\\Sessions\\Web%20Server]\r\n"
            + "\"HostName\"=\"ops@web.example.com\"\r\n\"PortNumber\"=dword:000008ae\r\n\"Protocol\"=\"ssh\"\r\n"
            + "\"PublicKeyFile\"=\"C:\\\\keys\\\\web.ppk\"\r\n\r\n"
            + "[HKEY_CURRENT_USER\\Software\\SimonTatham\\PuTTY\\Sessions\\Old]\r\n\"HostName\"=\"old\"\r\n\"Protocol\"=\"telnet\"\r\n";

        var result = PuttyRegImporter.Parse(text);

        var web = Assert.Single(result.Connections);
        Assert.Equal("Web Server", web.Name);
        Assert.Equal("web.example.com", web.Host);
        Assert.Equal(2222, web.Port);
        Assert.Equal("ops", web.Options!["username"]);
        Assert.Equal("C:\\keys\\web.ppk", web.Options["privateKeyPath"]);
        Assert.Contains(result.Warnings, w => w.Contains("Old"));
    }

    [Fact]
    public void Rdp_ReadsAddressPortAndUser()
    {
        var result = RdpFileImporter.Parse("screen mode id:i:2\r\nfull address:s:srv01:3390\r\nusername:s:admin\r\ndomain:s:CORP\r\n", "Srv01");

        var c = Assert.Single(result.Connections);
        Assert.Equal("rdp", c.Type);
        Assert.Equal("srv01", c.Host);
        Assert.Equal(3390, c.Port);
        Assert.Equal("admin", c.Options!["username"]);
        Assert.Equal("CORP", c.Options["domain"]);
    }

    [Fact]
    public void Rdp_WithoutAnAddress_IsSkippedWithAWarning()
    {
        var result = RdpFileImporter.Parse("username:s:x\n", "Nothing");

        Assert.Empty(result.Connections);
        Assert.Single(result.Warnings);
    }

    [Fact]
    public void MRemoteNg_KeepsFolders_AndMapsProtocols()
    {
        const string xml = """
            <Connections Name="Connections" FullFileEncryption="false">
              <Node Name="Prod" Type="Container">
                <Node Name="web" Type="Connection" Protocol="SSH2" Hostname="web.example.com" Port="22" Username="root" Descr="main" />
                <Node Name="desk" Type="Connection" Protocol="RDP" Hostname="desk" Port="3390" Domain="CORP" />
                <Node Name="odd" Type="Connection" Protocol="ICA" Hostname="x" />
              </Node>
              <Node Name="top" Type="Connection" Protocol="VNC" Hostname="vnc01" Port="5900" />
            </Connections>
            """;

        var result = MRemoteNgImporter.Parse(xml);

        Assert.Equal("Prod", Assert.Single(result.Folders).Name);
        Assert.Equal(3, result.Connections.Count);
        var web = result.Connections.Single(c => c.Name == "web");
        Assert.Equal("ssh", web.Type);
        Assert.Null(web.Port);
        Assert.Equal("main", web.Notes);
        Assert.Equal(result.Folders[0].Id, web.FolderId);
        Assert.Equal(3390, result.Connections.Single(c => c.Name == "desk").Port);
        Assert.Null(result.Connections.Single(c => c.Name == "top").FolderId);
        Assert.Contains(result.Warnings, w => w.Contains("ICA"));
    }

    [Fact]
    public void MRemoteNg_EncryptedFile_IsRefused()
    {
        Assert.Throws<CatalogException>(() => MRemoteNgImporter.Parse("<Connections FullFileEncryption=\"true\" />"));
    }

    [Theory]
    [InlineData("not xml at all")]
    [InlineData("<Other />")]
    public void MRemoteNg_BadFiles_AreRefused(string text)
    {
        Assert.Throws<CatalogException>(() => MRemoteNgImporter.Parse(text));
    }

    [Fact]
    public void RdcMan_ReadsGroupsServersAndCredentialsWithoutPasswords()
    {
        const string xml = """
            <RDCMan schemaVersion="3">
              <file>
                <properties><name>Datacenter</name></properties>
                <group>
                  <properties><name>Web</name></properties>
                  <server>
                    <properties><name>web01.corp</name><displayName>Web 01</displayName></properties>
                    <logonCredentials inherit="None"><userName>admin</userName><domain>CORP</domain><password>SECRET</password></logonCredentials>
                    <connectionSettings inherit="None"><port>3390</port></connectionSettings>
                  </server>
                </group>
                <server><properties><name>jump</name></properties></server>
              </file>
            </RDCMan>
            """;

        var result = RdcManImporter.Parse(xml);

        Assert.Equal(new[] { "Datacenter", "Web" }, result.Folders.Select(f => f.Name));
        Assert.Equal(result.Folders[0].Id, result.Folders[1].ParentId);
        var web = result.Connections.Single(c => c.Name == "Web 01");
        Assert.Equal("web01.corp", web.Host);
        Assert.Equal(3390, web.Port);
        Assert.Equal("admin", web.Options!["username"]);
        Assert.DoesNotContain("SECRET", string.Join(' ', web.Options.Values));
        Assert.Equal("jump", result.Connections.Single(c => c.Name == "jump").Host);
    }

    [Fact]
    public void AddTo_PutsEverythingInTheStore()
    {
        var result = SshConfigImporter.Parse("Host a\n HostName h1\nHost b\n HostName h2\n", "/home/me");
        var store = new ConnectionStore();

        var problems = result.AddTo(store);

        Assert.Empty(problems);
        Assert.Equal(2, store.Connections.Count);
        Assert.Single(store.Folders);
        Assert.All(store.Connections, c => Assert.NotNull(c.FolderId));
    }

    [Fact]
    public void BadPorts_AreDroppedWithAWarning()
    {
        var result = SshConfigImporter.Parse("Host a\n Port 99999\n", "/home/me");

        Assert.Null(Assert.Single(result.Connections).Port);
        Assert.Contains(result.Warnings, w => w.Contains("99999"));
    }

    [Fact]
    public void FromFile_ChoosesTheImporterByName()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rd-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var rdp = Path.Combine(dir, "box.rdp");
            File.WriteAllText(rdp, "full address:s:box\n");
            var config = Path.Combine(dir, "config");
            File.WriteAllText(config, "Host x\n");
            var other = Path.Combine(dir, "notes.txt");
            File.WriteAllText(other, "hi");

            Assert.Equal("rdp", Assert.Single(ConnectionImport.FromFile(rdp, dir).Connections).Type);
            Assert.Equal("ssh", Assert.Single(ConnectionImport.FromFile(config, dir).Connections).Type);
            Assert.Throws<CatalogException>(() => ConnectionImport.FromFile(other, dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
