using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace network.gamehandoff;

/// <summary>
///     User Server와 Game Server에서 공통으로 사용하는 GameHandoff 기능을 DI에 등록한다.
///     ticket 서비스와 Redis 저장소를 연결하고, 발급 시 적용할 TTL의 기본값과 허용 범위를 통일한다.
/// </summary>
public static class GameHandoffServiceCollectionExtensions
{
    private const int DefaultGameHandoffLifetimeSeconds = 180;
    private const int MinimumGameHandoffLifetimeSeconds = 30;
    private const int MaximumGameHandoffLifetimeSeconds = 600;

    public static IServiceCollection AddGameHandoffTicket(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        int gameHandoffLifetimeSeconds = configuration.GetValue(
            "gameHandoff:ticketLifetimeSeconds",
            DefaultGameHandoffLifetimeSeconds);

        if (gameHandoffLifetimeSeconds is < MinimumGameHandoffLifetimeSeconds or > MaximumGameHandoffLifetimeSeconds)
        {
            throw new InvalidOperationException(
                $"gameHandoff:ticketLifetimeSeconds must be between " +
                $"{MinimumGameHandoffLifetimeSeconds} and {MaximumGameHandoffLifetimeSeconds} seconds.");
        }

        services.AddSingleton(new GameHandoffTicketOptions
        {
            Lifetime = TimeSpan.FromSeconds(gameHandoffLifetimeSeconds)
        });
        services.AddSingleton<IGameHandoffTicketStore, RedisGameHandoffTicketStore>();
        services.AddSingleton<IGameHandoffTicketService, GameHandoffTicketService>();
        return services;
    }
}
