using Microsoft.Extensions.Configuration;
using network.interfaces;

namespace network.infrastructure;

public class ServerConfig : IServerConfig
{
    public string ServerType { get; init; } = "";
    public int ServerId { get; init; }
    /// <summary>
    ///     Game Server 노드 식별자(설정 <c>gameServerId</c> 원문, 예: <c>game-server-0</c>).
    ///     레지스트리 필드 이름이자 handoff ticket이 결합되는 값이라 배포 단위에서 고정돼야 한다.
    /// </summary>
    public string GameServerNodeId { get; init; } = "";

    public int GameServerNum { get; init; }

    public void Validate()
    {
        if (ServerType == "GameServer" && ServerId <= 0)
        {
            throw new ArgumentException($"Invalid Game Server Id: {ServerId}");
        }
        if (ServerType == "GameServer" && string.IsNullOrWhiteSpace(GameServerNodeId))
        {
            throw new ArgumentException("gameServerId must name this Game Server node.");
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
