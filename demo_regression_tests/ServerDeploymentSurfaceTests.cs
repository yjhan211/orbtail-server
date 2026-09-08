using game_server.matches.combat;
using game_server.matches.results;
using game_server.matches;
namespace demo_regression_tests;

public sealed class ServerDeploymentSurfaceTests
{
    [Fact]
    public void GameServerDoesNotKeepUnusedAdminEntryPoints()
    {
        string source = Read("game_server/GameServer.cs");
        Assert.DoesNotContain("CreateBotOnlyInstance", source);
        Assert.DoesNotContain("GetInstanceSummary", source);
        Assert.DoesNotContain("GetFullInstanceSnapshot", source);
        Assert.DoesNotContain("GetInstanceSnapshot", source);
        Assert.DoesNotContain("GetActiveMatchingIds", source);
        Assert.DoesNotContain("GetActiveMatchingIds", Read("game_server/Matches/Combat/MatchArenaService.cs"));
        Assert.DoesNotContain("GetActiveMatchingIds", Read("game_server/Sessions/GameSessionRegistry.cs"));
        Assert.Contains("EndBotOnlyMatchIfSettled", Read("game_server/Matches/Results/MatchCleanupService.cs"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void CutDummyRejectsMissingMatchIdEvenWhenAnotherMatchExists(long matchingId)
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        var runtime = store.GetOrCreate(70001);
        var server = GameServerTestAccess.Create(store);

        object result = server.GetArena().SetupSwarmCutDummy(matchingId);

        Assert.Equal("no active match", result.GetType().GetProperty("error")!.GetValue(result));
        Assert.Same(runtime, store.GetOrNull(70001));
        Assert.False(runtime.IsEnded);
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
