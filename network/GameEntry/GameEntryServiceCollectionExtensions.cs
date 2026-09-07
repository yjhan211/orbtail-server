using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace network.gameentry;

/// <summary>
///     User Server와 Game Server에서 공통으로 사용하는 GameEntry 기능을 DI에 등록한다.
///     ticket 서비스와 Redis 저장소를 연결하고, 발급 시 적용할 TTL의 기본값과 허용 범위를 통일한다.
/// </summary>
public static class GameEntryServiceCollectionExtensions
{
    private const int DefaultGameEntryLifetimeSeconds = 180;
    private const int MinimumGameEntryLifetimeSeconds = 30;
    private const int MaximumGameEntryLifetimeSeconds = 600;

    public static IServiceCollection AddGameEntryTicket(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // 배포 설정과의 호환을 위해 외부 설정 키는 기존 이름을 유지한다.
        int gameEntryLifetimeSeconds = configuration.GetValue(
            "gameHandoff:ticketLifetimeSeconds",
            DefaultGameEntryLifetimeSeconds);

        if (gameEntryLifetimeSeconds is < MinimumGameEntryLifetimeSeconds or > MaximumGameEntryLifetimeSeconds)
        {
            throw new InvalidOperationException(
                $"gameHandoff:ticketLifetimeSeconds must be between " +
                $"{MinimumGameEntryLifetimeSeconds} and {MaximumGameEntryLifetimeSeconds} seconds.");
        }

        services.AddSingleton(new GameEntryTicketOptions
        {
            Lifetime = TimeSpan.FromSeconds(gameEntryLifetimeSeconds)
        });
        services.AddSingleton<IGameEntryTicketStore, RedisGameEntryTicketStore>();
        services.AddSingleton<GameEntryTicketService>();
        return services;
    }
}
