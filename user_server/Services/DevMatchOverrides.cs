using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;
using network.interfaces;

namespace user_server.services;

/// <summary>
///     개발·검증용 매칭 오버라이드 모음. 환경 변수(<c>TEST_TWO_PLAYER_MATCH</c>, <c>SOLO_MAP_VALIDATION</c>)는
///     생성 시 한 번만 읽는다. 정식 매칭 규칙은 이 클래스에 두지 않는다.
///     두 명 테스트 매치에서 두 번째 접속자에게 입히는 고정 코스튬 아이템 ID도 여기에서만 보유한다.
/// </summary>
internal sealed class DevMatchOverrides
{
    internal const string TwoPlayerTestMatchVariable = "TEST_TWO_PLAYER_MATCH";
    internal const string SoloMapValidationVariable = "SOLO_MAP_VALIDATION";
    internal const int DefaultPlayersPerMatch = 1;
    internal const int DefaultGamePlayersPerMatch = 8;

    /// <summary>
    ///     TEST_TWO_PLAYER_MATCH에서 두 번째 접속자에게 고정 착용시키는 코스튬 (Hair/Face/Glasses/Top/Bottom/Shoes).
    /// </summary>
    internal static readonly int[] TwoPlayerTestOutfitItemIds =
    {
        101000005,
        102000005,
        103000003,
        104000007,
        105000007,
        106000004
    };

    private readonly IRedisOperations _redisOperations;
    private readonly IRedLockFactory _redLock;
    private readonly ILogger _logger;

    public DevMatchOverrides(
        bool twoPlayerTestMatch,
        bool soloMapValidation,
        IRedisOperations redisOperations,
        IRedLockFactory redLock,
        ILogger logger)
    {
        IsTwoPlayerTestMatch = twoPlayerTestMatch;
        IsSoloMapValidation = soloMapValidation;
        _redisOperations = redisOperations;
        _redLock = redLock;
        _logger = logger;
    }

    public bool IsTwoPlayerTestMatch { get; }
    public bool IsSoloMapValidation { get; }
    public MatchMode MatchMode => IsSoloMapValidation ? MatchMode.SoloMapValidation : MatchMode.Normal;

    /// <summary>
    ///     한 그룹을 이루는 인간 수. 솔로 검증 1, 두 명 테스트 2, 기본 1.
    /// </summary>
    public int PlayersPerMatch => IsSoloMapValidation ? 1 : IsTwoPlayerTestMatch ? 2 : DefaultPlayersPerMatch;

    /// <summary>
    ///     봇 포함 매치 정원. 솔로 검증은 봇 없이 1인, 그 외는 swarm_config의 SWARM_PLAYERS_PER_MATCH.
    /// </summary>
    public int GamePlayersPerMatch => IsSoloMapValidation ? DefaultPlayersPerMatch : Config.SWARM_PLAYERS_PER_MATCH;

    /// <summary>
    ///     30초 장기 대기 봇 채움 pass는 두 명 테스트·솔로 검증에서는 실행하지 않는다.
    /// </summary>
    public bool AllowsBotFill => !IsTwoPlayerTestMatch && !IsSoloMapValidation;

    /// <summary>
    ///     환경 변수를 지금 한 번 읽어 고정한다.
    /// </summary>
    public static DevMatchOverrides FromEnvironment(IRedisOperations redisOperations, IRedLockFactory redLock, ILogger logger)
    {
        return new DevMatchOverrides(
            IsEnabled(TwoPlayerTestMatchVariable),
            IsEnabled(SoloMapValidationVariable),
            redisOperations,
            redLock,
            logger);
    }

    private static bool IsEnabled(string variable)
    {
        return Environment.GetEnvironmentVariable(variable) == "1";
    }

    /// <summary>
    ///     두 명 테스트 매치에서 두 번째 접속자의 착용 코스튬을 고정한다. 오버라이드가 꺼져 있으면 아무것도 하지 않는다.
    /// </summary>
    public async Task ApplyTwoPlayerTestOutfitAsync(IReadOnlyList<MatchingQueueEntry> entries)
    {
        if (!IsTwoPlayerTestMatch) return;

        var realPlayers = entries
            .Where(entry => entry.PlayerId >= 0)
            .OrderBy(entry => entry.RequestTime)
            .ThenBy(entry => entry.PlayerId)
            .ToList();

        if (realPlayers.Count < 2) return;

        long targetPlayerId = realPlayers[1].PlayerId;

        await using (await PlayerInfo.Lock(_redLock, targetPlayerId))
        {
            var targetPlayer = await PlayerInfo.Load(_redisOperations, targetPlayerId);
            if (targetPlayer == null)
            {
                _logger.LogWarning("Two-player outfit setup failed: target player load failed ({PlayerId})", targetPlayerId);
                return;
            }

            foreach (var item in targetPlayer.InventoryInfo.ItemDict.Values) item.IsWear = false;

            targetPlayer.WearItemIdList.Clear();
            foreach (int itemId in TwoPlayerTestOutfitItemIds)
            {
                var targetItem = targetPlayer.InventoryInfo.ItemDict.Values.FirstOrDefault(item => item.ItemId == itemId);
                if (targetItem == null)
                {
                    long itemUid = await _redisOperations.StringIncrementAsync("item_uid_counter");
                    targetItem = new ItemInfo(itemUid, itemId, 1);
                    targetPlayer.InventoryInfo.ItemDict.Add(targetItem.ItemUid, targetItem);
                }

                targetItem.IsWear = true;
                targetPlayer.WearItemIdList.Add(itemId);
            }

            await targetPlayer.Save(_redisOperations);
        }

        _logger.LogInformation("Two-player outfit fixed: Player2={PlayerId}, Items={Items}",
            targetPlayerId, string.Join(", ", TwoPlayerTestOutfitItemIds));
    }
}
