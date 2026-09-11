using game_server.matches;
using game_server.matches.combat;
namespace demo_regression_tests;

public sealed class ServerDeploymentSurfaceTests
{
    [Fact]
    public void RetiredGameServerDeveloperModesAreNotExposed()
    {
        Assert.Null(typeof(game_server.GameServerNodeOptions).Assembly.GetType("game_server.GameServerDevOptions"));
        foreach (string path in new[] { "docker-compose.local.yml", "docker-compose.scale.yml" })
        {
            string source = Read(path);
            foreach (string flag in new[] { "DISABLE_GAME_END", "DEV_CROSSFIRE_SANDBOX", "SOLO_MONSTERS", "DEV_CUT_DUMMY" })
                Assert.DoesNotContain(flag, source);
        }
        Assert.DoesNotContain("SetupSwarmCutDummy", Read("game_server/Matches/MatchCombatService.cs"));
        Assert.DoesNotContain("IsSwarmCutDummy", Read("game_server/Players/Bots/BotPlayerManager.cs"));
    }

    [Fact]
    public void GameServerDoesNotKeepUnusedAdminEntryPoints()
    {
        string source = Read("game_server/GameServer.cs");
        Assert.DoesNotContain("CreateBotOnlyInstance", source);
        Assert.DoesNotContain("GetInstanceSummary", source);
        Assert.DoesNotContain("GetFullInstanceSnapshot", source);
        Assert.DoesNotContain("GetInstanceSnapshot", source);
        Assert.DoesNotContain("GetActiveMatchingIds", source);
        Assert.DoesNotContain("GetActiveMatchingIds", Read("game_server/Matches/MatchCombatService.cs"));
        Assert.DoesNotContain("GetActiveMatchingIds", Read("game_server/Sessions/GameSessionRegistry.cs"));
        Assert.Contains("EndBotOnlyMatchIfSettled", Read("game_server/Matches/MatchCleanupService.cs"));
    }

    [Theory]
    [InlineData("server.sln")]
    [InlineData("docker-compose.local.yml")]
    [InlineData("docker-compose.prod.yml")]
    [InlineData("docker-compose.scale.yml")]
    public void ActiveBuildAndDeploymentDoNotReferenceRetiredOpsServer(string path)
    {
        string source = Read(path);
        Assert.DoesNotContain("ops_server", source);
        Assert.DoesNotContain("ops-server", source);
        Assert.DoesNotContain("GameServerUrl", source);
    }

    [Theory]
    [InlineData("user_server/HealthCheckService.cs")]
    [InlineData("game_server/HealthCheckService.cs")]
    public void ServersKeepHealthAndMetrics(string path)
    {
        string source = Read(path);
        Assert.Contains("\"/health/live\"", source);
        Assert.Contains("\"/health/ready\"", source);
        Assert.Contains("MapMetrics()", source);
    }

    [Fact]
    public void GameServerHealthHostDoesNotDependOnGameServerOrExposeAdminRoutes()
    {
        string source = Read("game_server/HealthCheckService.cs");
        Assert.DoesNotContain("MapAdminEndpoints", source);
        Assert.DoesNotContain("GameServer gameServer", source);
        Assert.DoesNotContain("/admin/", source);
    }

    private static string Read(string path)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "server.sln")))
            directory = directory.Parent;
        if (directory == null) throw new DirectoryNotFoundException("Repository root not found.");
        return File.ReadAllText(Path.Combine(directory.FullName, path));
    }
}
