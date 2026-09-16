namespace demo_regression_tests;

public sealed class ServerDeploymentSurfaceTests
{
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

    private static string Read(string path)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "server.sln")))
            directory = directory.Parent;
        if (directory == null) throw new DirectoryNotFoundException("Repository root not found.");
        return File.ReadAllText(Path.Combine(directory.FullName, path));
    }
}
