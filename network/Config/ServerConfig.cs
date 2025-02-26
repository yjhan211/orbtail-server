using Microsoft.Extensions.Configuration;

namespace network.config;

public class ServerConfig
{
    public string ServerType { get; init; } = "";
    public int GameServerNum { get; init; }
    public int GameServerId { get; init; }
    
    public void Validate()
    {
        if (GameServerId <= 0)
        {
            throw new ArgumentException($"Invalid Game Server Id: {GameServerId}");
        }
        if (ServerType == "GameServer" && GameServerNum <= 0)
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
