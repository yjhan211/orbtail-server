using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using network.interfaces;

namespace network.gamehandoff;

public static class GameHandoffServiceCollectionExtensions
{
    private const int DefaultGameHandoffLifetimeSeconds = 180;
    private const int MinimumGameHandoffLifetimeSeconds = 30;
    private const int MaximumGameHandoffLifetimeSeconds = 600;

    /// <summary>두 서버가 공유하는 handoff ticket 계약(발급·소비·Redis 저장소)만 등록한다. 계정 인증은 user_server의 몫이다.</summary>
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
