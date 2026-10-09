using RemoteDeck.Core.Import;

namespace RemoteDeck.Core.Tests;

public class MobaXtermImportTests
{
    private const string Sample = """
        [Bookmarks]
        SubRep=
        ImgNum=42
        Jump box=#109#0%jump.example.com%2222%[admin]%%-1%-1%%%%%0%0%0%%%-1%0%0%0%%1080%%0%0%1%#MobaFont%10%0%0%-1%15%236,236,236%30,30,30%180,180,180%0%-1%0%%xterm%-1%-1%_Std_Colors_0_%80%24%0%1%-1%<none>%%0#0# #-1

        [Bookmarks_1]
        SubRep=Lab\Switches
        ImgNum=41
        core-sw=#98#1%10.0.0.2%23%%%-1%-1%#0
        old-vnc=#128#5%10.0.0.9%5900%%%-1%-1%#0

        [Bookmarks_2]
        SubRep=Lab
        ImgNum=41
        desktop=#91#4%10.0.0.7%3390%<default>%%-1%-1%#0
        plain=#109#0%10.0.0.8%22%%%-1%-1%#0
        """;

    [Fact]
    public void ReadsSshTelnetAndRdp_WithFolders()
    {
        var result = MobaXtermImporter.Parse(Sample);

        Assert.Equal(new[] { "Jump box", "core-sw", "desktop", "plain" }, result.Connections.Select(c => c.Name));
        var jump = result.Connections[0];
        Assert.Equal("ssh", jump.Type);
        Assert.Equal("jump.example.com", jump.Host);
        Assert.Equal(2222, jump.Port);
        Assert.Equal("admin", jump.Options!["username"]);
        Assert.Contains(result.Warnings, w => w.Contains("password manager") && w.Contains("admin"));

        var telnet = result.Connections[1];
        Assert.Equal("telnet", telnet.Type);
        Assert.Null(telnet.Port);

        var rdp = result.Connections[2];
        Assert.Equal("rdp", rdp.Type);
        Assert.Equal(3390, rdp.Port);
        Assert.Null(rdp.Options);

        Assert.Null(result.Connections[3].Port);
    }

    [Fact]
    public void ARealExportLayoutIsReadByPosition()
    {
        const string text = "[Bookmarks]\nSubRep=\nImgNum=42\n192.168.10.82=#109#0%192.168.10.82%22%%%-1%-1%%%%%0%-1%0#MobaFont%10#0# #-1\n\n"
            + "[Bookmarks_1]\nSubRep=Lab Machines\nImgNum=41\nNAS=#109#0%192.168.10.58%22%[mickey]%%-1%-1%#0\nPlex=#91#4%192.168.10.50%3389%[plex]%0%0%0#MobaFont%10#0# #-1\n";

        var result = MobaXtermImporter.Parse(text);

        Assert.Equal(3, result.Connections.Count);
        Assert.Equal("192.168.10.82", result.Connections[0].Name);
        Assert.Null(result.Connections[0].Options);
        Assert.Equal("mickey", result.Connections[1].Options!["username"]);
        Assert.Equal("rdp", result.Connections[2].Type);
        Assert.Equal("plex", result.Connections[2].Options!["username"]);
        Assert.Contains(result.Folders, f => f.Name == "Lab Machines");
    }

    [Fact]
    public void FoldersNestUnderAMobaXtermRoot()
    {
        var result = MobaXtermImporter.Parse(Sample);

        var root = result.Folders.Single(f => f.Name == "MobaXterm");
        var lab = result.Folders.Single(f => f.Name == "Lab");
        var switches = result.Folders.Single(f => f.Name == "Switches");
        Assert.Equal(root.Id, lab.ParentId);
        Assert.Equal(lab.Id, switches.ParentId);
        Assert.Equal(root.Id, result.Connections[0].FolderId);
        Assert.Equal(switches.Id, result.Connections[1].FolderId);
        Assert.Equal(lab.Id, result.Connections[2].FolderId);
    }

    [Fact]
    public void UnsupportedTypesAreSkippedWithANote()
    {
        var result = MobaXtermImporter.Parse(Sample);

        Assert.DoesNotContain(result.Connections, c => c.Name == "old-vnc");
        Assert.Contains(result.Warnings, w => w.Contains("128"));
    }

    [Fact]
    public void IgnoresOtherSectionsOfAFullConfig()
    {
        var result = MobaXtermImporter.Parse("[Misc]\nfoo=#109#0%x%22%u%\n[Bookmarks]\nSubRep=\nImgNum=42\na=#109#0%h%22%u%%\n");

        Assert.Equal("a", result.Connections.Single().Name);
    }

    [Fact]
    public void ADotIniFileIsOnlyUsedWhenItHasBookmarks()
    {
        Assert.True(MobaXtermImporter.LooksLikeMobaXterm(Sample));
        Assert.False(MobaXtermImporter.LooksLikeMobaXterm("[Misc]\nx=1"));
    }

    [Fact]
    public void ImportFromFile_PicksTheMobaXtermImporter()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".mxtsessions");
        File.WriteAllText(path, Sample);
        try
        {
            Assert.Equal(4, ConnectionImport.FromFile(path, "C:/Users/me").Connections.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
