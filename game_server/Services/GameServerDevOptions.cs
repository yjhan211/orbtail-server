using Microsoft.Extensions.Configuration;

namespace game_server.services;

/// <summary>
///     Game Server 프로세스에만 적용되는 개발·검증 옵션. 환경 변수는 서버 기동 시 한 번 읽고 이후에는 바뀌지 않는다.
///     매치 구성까지 바꾸는 SOLO_MAP_VALIDATION은 여기에 두지 않고 User Server가 MatchManifest로 전달한다.
/// </summary>
public sealed class GameServerDevOptions
{
    public const string DisableGameEndVariable = "DISABLE_GAME_END";
    public const string CrossfireSandboxVariable = "DEV_CROSSFIRE_SANDBOX";
    public const string DisableMonstersVariable = "DEV_NO_MONSTERS";
    public const string SoloMonstersVariable = "SOLO_MONSTERS";
    public const string CutDummyVariable = "DEV_CUT_DUMMY";

    public static GameServerDevOptions Disabled { get; } = new();

    public bool DisableGameEnd { get; init; }
    public bool CrossfireSandbox { get; init; }
    public bool DisableMonsters { get; init; }
    public bool SoloMonsters { get; init; }
    public bool CutDummy { get; init; }
    public bool MonsterSpawnEnabled => !DisableMonsters;

    public static GameServerDevOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return new GameServerDevOptions
        {
            DisableGameEnd = IsEnabled(configuration, DisableGameEndVariable),
            CrossfireSandbox = IsEnabled(configuration, CrossfireSandboxVariable),
            DisableMonsters = IsEnabled(configuration, DisableMonstersVariable),
            SoloMonsters = IsEnabled(configuration, SoloMonstersVariable),
            CutDummy = IsEnabled(configuration, CutDummyVariable)
        };
    }

    public void Validate(bool isDevelopmentEnvironment)
    {
        if (CrossfireSandbox && CutDummy)
            throw new InvalidOperationException(
                $"{CrossfireSandboxVariable} and {CutDummyVariable} cannot both be enabled.");

        if (DisableMonsters && SoloMonsters)
            throw new InvalidOperationException(
                $"{DisableMonstersVariable} and {SoloMonstersVariable} cannot both be enabled.");

        if (isDevelopmentEnvironment)
            return;

        IReadOnlyList<string> enabledVariables = EnabledVariableNames();
        if (enabledVariables.Count > 0)
            throw new InvalidOperationException(
                $"Game Server development flags cannot be enabled outside Development: {string.Join(", ", enabledVariables)}");
    }

    public IReadOnlyList<string> EnabledVariableNames()
    {
        var names = new List<string>(5);
        if (DisableGameEnd) names.Add(DisableGameEndVariable);
        if (CrossfireSandbox) names.Add(CrossfireSandboxVariable);
        if (DisableMonsters) names.Add(DisableMonstersVariable);
        if (SoloMonsters) names.Add(SoloMonstersVariable);
        if (CutDummy) names.Add(CutDummyVariable);
        return names;
    }

    private static bool IsEnabled(IConfiguration configuration, string variable) =>
        string.Equals(configuration[variable], "1", StringComparison.Ordinal);
}
