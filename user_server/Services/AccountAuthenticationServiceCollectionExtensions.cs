using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace user_server.services;

/// <summary>
///     계정 토큰 인증(불투명 토큰 ↔ playerId 매핑)은 로그인을 받는 user_server만의 일이다 — game_server는 handoff ticket만 믿는다.
/// </summary>
public static class AccountAuthenticationServiceCollectionExtensions
{
    public static IServiceCollection AddAccountAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        bool allowLegacyNumericMigration = configuration.GetValue(
            "accountToken:allowLegacyNumericMigration",
            false);
        string? legacyMigrationSecret = configuration["accountToken:legacyMigrationSecret"];

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
        services.AddSingleton<IAccountCredentialStore, RedisAccountCredentialStore>();
        services.AddSingleton<IAccountTokenService, AccountTokenService>();
        return services;
    }
}
