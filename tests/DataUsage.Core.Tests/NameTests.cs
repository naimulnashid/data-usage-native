using DataUsage.Core.Naming;

namespace DataUsage.Core.Tests;

public class NameTests
{
    [Theory]
    [InlineData(@"\device\harddiskvolume4\program files\qbittorrent\qbittorrent.exe", "path", "qbittorrent.exe", "qBittorrent")]
    [InlineData("OpenAI.Codex_26.814.5517.0_x64__2p2nqsd0c76g0", "appx", "chatgpt", "ChatGPT")]
    [InlineData("OpenAI.Codex_26.810.4967.0_x64__2p2nqsd0c76g0", "appx", "chatgpt", "ChatGPT")]
    [InlineData("DoSvc", "service", "windows-update", "System and Windows Update")]
    [InlineData("BITS", "service", "windows-update", "System and Windows Update")]
    [InlineData(@"\device\harddiskvolume4\users\x\appdata\local\anthropic\claude.exe", "path", "claude", "Claude")]
    [InlineData("Claude_1.0.0.0_x64__abc123", "appx", "claude", "Claude")]
    [InlineData("powershell.exe", "service", "svc:powershell.exe", "powershell.exe")]
    public void ResolvesToTheFamilyWindowsShows(string identity, string kind, string group, string name)
    {
        var r = AppNames.Resolve(identity, kind);
        Assert.Equal(group, r.GroupKey);
        Assert.Equal(name, r.DisplayName);
    }

    [Fact]
    public void MembersStayDistinctInsideAFamily()
    {
        var cli = AppNames.Resolve(@"\device\harddiskvolume4\x\claude.exe", "path");
        var desktop = AppNames.Resolve("Claude_1.0.0.0_x64__abc123", "appx");
        Assert.Equal(cli.GroupKey, desktop.GroupKey);
        Assert.Equal("Claude Code", cli.MemberName);
        Assert.Equal("Claude Desktop", desktop.MemberName);
    }

    [Fact]
    public void GenericBasenamesAreKeyedByDirectory()
    {
        var vs = AppNames.Resolve(@"\device\harddiskvolume4\program files (x86)\microsoft visual studio\installer\setup.exe", "path");
        var nv = AppNames.Resolve(@"\device\harddiskvolume4\program files\nvidia corporation\installer2\setup.exe", "path");
        var other = AppNames.Resolve(@"\device\harddiskvolume4\users\x\downloads\thing\setup.exe", "path");
        Assert.Equal("visual-studio", vs.GroupKey);
        Assert.Equal("nvidia", nv.GroupKey);
        Assert.Equal(@"thing\setup.exe", other.GroupKey);
    }

    [Fact]
    public void AVendorRuleAppliesEvenToAKnownBasename()
    {
        // Else the app and its installer were two groups called "Bitwarden".
        var installer = AppNames.Resolve(@"\device\harddiskvolume4\users\x\appdata\local\temp\bitwarden-installer-1\bitwarden.exe", "path");
        Assert.Equal("bitwarden", installer.GroupKey);
    }

    [Theory]
    [InlineData("GoogleUpdaterService151.0.7910.0", "GoogleUpdaterService")]
    [InlineData("WpnUserService_ca529", "wpnuserservice")]
    [InlineData("windows.immersivecontrolpanel_10.0.8.1000_neutral_neutral_cw5n1h2txyewy", "windows.immersivecontrolpanel")]
    [InlineData("Tcpip6", "Tcpip6")]
    public void ServiceNoiseIsStripped(string raw, string expected) => Assert.Equal(expected, AppNames.NormaliseService(raw));

    [Fact]
    public void EveryFamilyNameIsUniqueAcrossTheKnownTables()
    {
        // The colour and logo maps key on display name: two families sharing
        // one would silently share both.
        var samples = new[]
        {
            ("powershell.exe", "path"), ("Microsoft.PowerShell_7.6.6.0_x64__8wekyb3d8bbwe", "appx"),
            (@"\x\bitwarden.exe", "path"), (@"\x\bitwarden-installer\setup.exe", "path"),
            (@"\x\wispr flow.exe", "path"), (@"\x\wisprflow\update.exe", "path"),
        };
        var byName = samples.Select(s => AppNames.Resolve(s.Item1, s.Item2)).GroupBy(r => r.DisplayName);
        Assert.All(byName, g => Assert.Single(g.Select(r => r.GroupKey).Distinct()));
    }
}
