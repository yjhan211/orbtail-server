using System.Text.RegularExpressions;

namespace demo_regression_tests;

public sealed class ClientAccountCredentialContractTests
{
    [Fact]
    public void LoginKeepsOpaqueCredentialAndDoesNotFallbackToNumericId()
    {
        string source = Read("client/Assets/Scripts/GameUser.Network.cs");
        Assert.DoesNotContain("LegacyAccountPlayerId", source);
        Assert.DoesNotContain("AccountTokenPending", source);
        Assert.DoesNotContain("PlayerPrefs.DeleteKey(PlayerPrefsKeys.AccountToken)", source);
        Assert.Contains("string.IsNullOrWhiteSpace(packet.AccountToken)", source);
        int response = source.IndexOf("private void ReceiveLogin(", StringComparison.Ordinal);
        int guard = source.IndexOf("string.IsNullOrWhiteSpace(packet.AccountToken)", response, StringComparison.Ordinal);
        int exit = source.IndexOf("return;", guard, StringComparison.Ordinal);
        int save = source.IndexOf("PlayerPrefs.SetString(PlayerPrefsKeys.AccountToken, packet.AccountToken)", response, StringComparison.Ordinal);
        Assert.True(response >= 0 && guard > response && exit > guard && save > exit);
        string credential = source[source.IndexOf("private static string GetStoredAccountCredential()", StringComparison.Ordinal)..];
        int saved = credential.IndexOf("PlayerPrefs.Save()", StringComparison.Ordinal);
        int generated = credential.IndexOf("accountToken = \"acct_\"", StringComparison.Ordinal);
        Assert.True(generated >= 0 && saved > generated);
    }

    [Fact]
    public void SettingsCannotChangeOrEraseAccountCredential()
    {
        string settings = Read("client/Assets/Scripts/UserInterfaces/SettingsPanel.cs");
        Assert.DoesNotContain("accountIdInput", settings);
        Assert.DoesNotContain("PlayerPrefsKeys.AccountToken", settings);
        string keys = Read("client/Assets/Scripts/Constants/PlayerPrefsKeys.cs");
        Assert.DoesNotContain("LegacyAccountPlayerId", keys);
        Assert.DoesNotContain("AccountTokenPending", keys);
    }

    [Theory]
    [InlineData("Login")]
    [InlineData("Camp")]
    [InlineData("School")]
    [InlineData("School2")]
    public void ScenesNoLongerContainAccountIdInput(string scene)
    {
        string source = Read($"client/Assets/Scenes/{scene}.unity");
        Assert.DoesNotContain("accountIdInput:", source);
        Assert.DoesNotContain("m_Name: PlayerIDBlock", source);
        Assert.DoesNotContain("m_Name: PlayerIDInput", source);
        Assert.Contains("guid: 77b41a964cecfe841b85828a55ddac70", source);
        var definitions = Regex.Matches(source, @"(?m)^--- !u!\d+ &(\d+)")
            .Select(match => match.Groups[1].Value).ToArray();
        Assert.Equal(definitions.Length, definitions.Distinct().Count());
    }

    private static string Read(string path)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "server.sln")))
            directory = directory.Parent;
        if (directory == null) throw new DirectoryNotFoundException("Repository root not found.");
        return File.ReadAllText(Path.Combine(directory.FullName, path)).Replace("\r\n", "\n");
    }
}
