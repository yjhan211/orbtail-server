using Microsoft.Extensions.Configuration;
using network.interfaces;

namespace network.infrastructure;

public class ServerConfig : IServerConfig
{
    public string ServerType { get; init; } = "";
    public int ServerId { get; init; }
    public int GameServerNum { get; init; }

    public void Validate()
    {
        if (ServerType == "GameServer" && ServerId <= 0)
        {
            throw new ArgumentException($"Invalid Game Server Id: {ServerId}");
        }
        if (GameServerNum <= 0)
        {
            throw new ArgumentException($"Invalid Game Server Number: {GameServerNum}");
        }
    }
}

public static class ConfigUtil
{
    public static string GetRequiredString(this IConfiguration configuration, string key)
    {
        return configuration[key] ??
               throw new InvalidOperationException($"{key} is not configured.");
    }
}
