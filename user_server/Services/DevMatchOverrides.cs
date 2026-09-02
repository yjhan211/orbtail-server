using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;
using network.interfaces;

namespace user_server.services;

/// <summary>
///     개발·검증용 매칭 오버라이드 모음. 환경 변수(<c>TEST_TWO_PLAYER_MATCH</c>, <c>SOLO_MAP_VALIDATION</c>,
///     <c>DEV_CROSSFIRE_SANDBOX</c>)는 생성 시 한 번만 읽는다. 정식 매칭 규칙은 이 클래스에 두지 않는다.
///     두 명 테스트 매치의 고정 타겟 코스튬 아이템 ID도 여기에서만 보유한다.
/// </summary>
internal sealed class DevMatchOverrides
{
    internal const string TwoPlayerTestMatchVariable = "TEST_TWO_PLAYER_MATCH";
    internal const string SoloMapValidationVariable = "SOLO_MAP_VALIDATION";
    internal const string CrossfireSandboxVariable = "DEV_CROSSFIRE_SANDBOX";
    internal const int DefaultPlayersPerMatch = 1;
    internal const int DefaultGamePlayersPerMatch = 8;
    private const int TwoPlayerTestRealPlayerCount = 2;

    /// <summary>
    ///     TEST_TWO_PLAYER_MATCH에서 두 번째 접속자에게 고정 착용시키는 코스튬 (Hair/Face/Glasses/Top/Bottom/Shoes).
    /// </summary>
    internal static readonly int[] TwoPlayerTargetOutfitItemIds =
    {
        101000005,
        102000005,
        103000003,
        104000007,
        105000007,
        106000004
    };

    private readonly ICacheHelper _cacheHelper;
    private readonly IRedLockFactory _redLock;
    private readonly ILogger _logger;

    public DevMatchOverrides(
        bool twoPlayerTestMatch,
        bool soloMapValidation,
        bool crossfireSandbox,
        ICacheHelper cacheHelper,
        IRedLockFactory redLock,
        ILogger logger)
    {
        IsTwoPlayerTestMatch = twoPlayerTestMatch;
        IsSoloMapValidation = soloMapValidation;
        IsCrossfireSandbox = crossfireSandbox;
        _cacheHelper = cacheHelper;
        _redLock = redLock;
        _logger = logger;
    }

    public bool IsTwoPlayerTestMatch { get; }
    public bool IsSoloMapValidation { get; }
    public bool IsCrossfireSandbox { get; }

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
    public static DevMatchOverrides FromEnvironment(ICacheHelper cacheHelper, IRedLockFactory redLock, ILogger logger)
    {
        return new DevMatchOverrides(
            IsEnabled(TwoPlayerTestMatchVariable),
            IsEnabled(SoloMapValidationVariable),
            IsEnabled(CrossfireSandboxVariable),
            cacheHelper,
            redLock,
            logger);
    }

    private static bool IsEnabled(string variable)
    {
        return Environment.GetEnvironmentVariable(variable) == "1";
    }

    /// <summary>
    ///     교차사격 샌드박스 (#232 2단계): DEV_CROSSFIRE_SANDBOX=1 이면 전원 운동장 스폰 —
    ///     게임서버가 첫 틱에 봇 하나를 더미로 세우고 나머지를 퇴장시킨다. 방 문이 잠긴 채
    ///     시작하는 정식 흐름에서는 사람이 운동장까지 나오는 데 100초가 걸린다.
    /// </summary>
    public Cell? GetSandboxSpawnCell()
    {
        return IsCrossfireSandbox
            ? GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, Config.SWARM_MATCH_GROUND_AREA)
            : null;
    }

    /// <summary>
    ///     두 명 테스트 매치의 결정적 체인: 요청 순 사람 2명 → 봇 6명 순서로 원형 체인을 만든다.
    ///     구성이 2+6이 아니면 예외를 던진다.
    /// </summary>
    public List<RosterChainLink> BuildTwoPlayerTestRosterChain(IReadOnlyList<MatchingQueueEntry> groupEntries)
    {
        var realPlayers = groupEntries
            .Where(entry => entry.PlayerId >= 0)
            .OrderBy(entry => entry.RequestTime)
            .ThenBy(entry => entry.PlayerId)
            .ToList();
        var bots = groupEntries.Where(entry => entry.IsBot).ToList();

        int expectedBotCount = DefaultGamePlayersPerMatch - TwoPlayerTestRealPlayerCount;
        if (realPlayers.Count != TwoPlayerTestRealPlayerCount || bots.Count != expectedBotCount)
        {
            _logger.LogWarning(
                "TEST_TWO_PLAYER_MATCH composition invalid: Real={Real}, Bots={Bot}; using normal-chain validation",
                realPlayers.Count, bots.Count);
            throw new InvalidOperationException("TEST_TWO_PLAYER_MATCH requires two real players and six bots.");
        }

        var ordered = realPlayers.Concat(bots).ToList();
        var chain = new List<RosterChainLink>(ordered.Count);
        for (int i = 0; i < ordered.Count; i++)
        {
            int targetIndex = (i + 1) % ordered.Count;
            chain.Add(new RosterChainLink(ordered[i], ordered[targetIndex].PlayerId));
        }

        _logger.LogInformation(
            "TEST_TWO_PLAYER_MATCH deterministic chain: {Chain}",
            MatchRosterBuilder.DescribeChain(ordered));

        return chain;
    }

    /// <summary>
    ///     두 명 테스트 매치에서 두 번째 접속자의 착용 코스튬을 고정한다. 오버라이드가 꺼져 있으면 아무것도 하지 않는다.
    /// </summary>
    public async Task ApplyTwoPlayerTestTargetOutfitAsync(List<RosterChainLink> chain)
    {
        if (!IsTwoPlayerTestMatch) return;

        var realPlayers = chain
            .Select(link => link.Entry)
            .Where(entry => entry.PlayerId >= 0)
            .OrderBy(entry => entry.RequestTime)
            .ThenBy(entry => entry.PlayerId)
            .ToList();

        if (realPlayers.Count < 2) return;

        long targetPlayerId = realPlayers[1].PlayerId;

        await using (await PlayerInfo.Lock(_redLock, targetPlayerId))
        {
            var targetPlayer = await PlayerInfo.Load(_cacheHelper, targetPlayerId);
            if (targetPlayer == null)
            {
                _logger.LogWarning("Two-player outfit setup failed: target player load failed ({PlayerId})", targetPlayerId);
                return;
            }

            foreach (var item in targetPlayer.InventoryInfo.ItemDict.Values) item.IsWear = false;

            targetPlayer.WearItemIdList.Clear();
            foreach (int itemId in TwoPlayerTargetOutfitItemIds)
            {
                var targetItem = targetPlayer.InventoryInfo.ItemDict.Values.FirstOrDefault(item => item.ItemId == itemId);
                if (targetItem == null)
                {
                    long itemUid = await _cacheHelper.StringIncrementAsync("item_uid_counter");
                    targetItem = new ItemInfo(itemUid, itemId, 1);
                    targetPlayer.InventoryInfo.ItemDict.Add(targetItem.ItemUid, targetItem);
                }

                targetItem.IsWear = true;
                targetPlayer.WearItemIdList.Add(itemId);
            }

            await targetPlayer.Save(_cacheHelper);
        }

        _logger.LogInformation("Two-player target outfit fixed: Player2={TargetPlayerId}, Items={Items}",
            targetPlayerId, string.Join(", ", TwoPlayerTargetOutfitItemIds));
    }
}
