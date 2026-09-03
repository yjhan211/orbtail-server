using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using network.application.authentication;
using network.contracts.authentication;
using network.interfaces;

namespace network.infrastructure.authentication;

public static class AuthenticationServiceCollectionExtensions
{
    private const int DefaultGameHandoffLifetimeSeconds = 180;
    private const int MinimumGameHandoffLifetimeSeconds = 30;
    private const int MaximumGameHandoffLifetimeSeconds = 600;

    public static IServiceCollection AddAuthenticationBoundaries(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        bool allowLegacyNumericMigration = configuration.GetValue(
            "accountToken:allowLegacyNumericMigration",
            false);
        string? legacyMigrationSecret = configuration["accountToken:legacyMigrationSecret"];
        int gameHandoffLifetimeSeconds = configuration.GetValue(
            "gameHandoff:ticketLifetimeSeconds",
            DefaultGameHandoffLifetimeSeconds);

        if (gameHandoffLifetimeSeconds is < MinimumGameHandoffLifetimeSeconds or > MaximumGameHandoffLifetimeSeconds)
        {
            throw new InvalidOperationException(
                $"gameHandoff:ticketLifetimeSeconds must be between " +
                $"{MinimumGameHandoffLifetimeSeconds} and {MaximumGameHandoffLifetimeSeconds} seconds.");
        }

        if (allowLegacyNumericMigration &&
            (string.IsNullOrWhiteSpace(legacyMigrationSecret) ||
             Encoding.UTF8.GetByteCount(legacyMigrationSecret) < 32))
        {
            throw new InvalidOperationException(
                "accountToken:legacyMigrationSecret must contain at least 32 UTF-8 bytes when legacy migration is enabled.");
        }

        services.AddSingleton(new AccountTokenOptions
        {
            AllowLegacyNumericMigration = allowLegacyNumericMigration,
            LegacyMigrationSecret = legacyMigrationSecret
        });
        services.AddSingleton(new GameHandoffTicketOptions
        {
            Lifetime = TimeSpan.FromSeconds(gameHandoffLifetimeSeconds)
        });
        services.AddSingleton<IAccountCredentialStore, RedisAccountCredentialStore>();
        services.AddSingleton<IAccountTokenService, AccountTokenService>();
        services.AddSingleton<IGameHandoffTicketStore, RedisGameHandoffTicketStore>();
        services.AddSingleton<IGameHandoffTicketService, GameHandoffTicketService>();
        return services;
    }
}
