using game_server;
using Microsoft.Extensions.Configuration;

namespace server_tests;

public sealed class GameServerPortTests
{
    [Fact]
    public void MissingPortUsesDefault()
    {
        var configuration = new ConfigurationBuilder().Build();
        Assert.Equal(9001, GameServer.ResolveServicePort(configuration));
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("9001", 9001)]
    [InlineData("32768", 32768)]
    [InlineData("65535", 65535)]
    public void AcceptsEntireTcpPortRange(string value, int expected)
    {
        Assert.Equal(expected, GameServer.ResolveServicePort(Configuration(value)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1.5")]
    [InlineData("-1")]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("2147483648")]
    public void RejectsInvalidPort(string value)
    {
        Assert.Throws<InvalidOperationException>(() => GameServer.ResolveServicePort(Configuration(value)));
    }

    private static IConfiguration Configuration(string value) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["clientPort"] = value })
            .Build();
}
