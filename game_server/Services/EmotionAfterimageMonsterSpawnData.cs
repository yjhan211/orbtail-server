using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.services;

/// <summary>
/// Fixed room packs and corridor pressure anchors for the emotion-afterimage loop.
/// Every room begins with normal afterimages while four distributed rooms also
/// expose a core afterimage as a premium public hotspot.
/// </summary>
internal static class EmotionAfterimageMonsterSpawnData
{
    private const int HopeT1 = 107000010;
    private const int ForgetT1 = 107000020;
    private const int DespairT1 = 107000030;
    private const int PackSize = 9;
    private const int ReinforcementSlotsPerArea = 10;
    private const int DefaultRoomAffinity = HopeT1;
    // Recovery remains a player-board option, but it has no combat identity for
    // afterimages. Monster packs only spawn the three attack affinities.
    private static readonly int[] AllAffinityItemIds = [HopeT1, ForgetT1, DespairT1];
    // #272 School2: 개전 핵 핫스팟 = 합류 구역 4곳 (동서남북 분산 — 공용 프리미엄 표적).
    private static readonly AreaType[] InitialHotspotAreas =
    [
        AreaType.S2Library1,
        AreaType.S2Library2,
        AreaType.S2Gym1,
        AreaType.S2Gym2
    ];

    public static readonly MonsterDefinition[] Definitions = CreateDefinitions();
    // Initial 4, then the room-closure waves keep at most 4 -> 3 -> 2 -> 1 -> 1
    // active core hotspots. Existing live hotspots are never removed just to hit a cap.
    private static readonly int[] WaveActiveAreaTargets = [4, 3, 2, 1, 1];
    private static readonly TimeSpan[] WavePackReleaseDurations =
    [
        TimeSpan.FromSeconds(20),
        TimeSpan.FromSeconds(20),
        TimeSpan.FromSeconds(20),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(15)
    ];
    // 몸은 무한, 지갑은 유한. 증원 몸 예산은 없다 — 생존 상한 아래로 떨어지면 계속 리필한다.
    // 대신 방·페이즈당 보상 예산을 두어, 예산 안의 처치만 소환석을 지급한다. 예산이 마르면
    // 몸은 계속 나오되 돈이 되지 않아, 위험만 남은 방을 떠날 이유가 생긴다. 핵 보상은 예산 외.
    private static readonly int[] AreaRewardBudgets = [10, 11, 12, 13];
    // 유리 떼: 스테이지가 오를수록 동시 상한이 올라 화면이 차오른다. HP는 12로 유지해
    // 성장한 플레이어의 쓸어버리는 감각을 지킨다. 위협은 양과 데미지에서 온다.
    private static readonly int[] ReinforcementAliveTargets = [9, 12, 15, 18];

    public const int ReinforcementBatchSize = 2;
    public static readonly TimeSpan ReinforcementReleaseInterval = TimeSpan.FromSeconds(1.5);

    public static int GetAreaRewardBudget(int closurePhase) =>
        AreaRewardBudgets[Math.Clamp(closurePhase, 0, AreaRewardBudgets.Length - 1)];

    public static int GetReinforcementAliveTarget(int closurePhase) =>
        ReinforcementAliveTargets[Math.Clamp(closurePhase, 0, ReinforcementAliveTargets.Length - 1)];

    public static int GetWaveActiveAreaTarget(int waveIndex) =>
        waveIndex >= 0 && waveIndex < WaveActiveAreaTargets.Length ? WaveActiveAreaTargets[waveIndex] : 0;

    public static TimeSpan GetWavePackReleaseDuration(int waveIndex) =>
        waveIndex >= 0 && waveIndex < WavePackReleaseDurations.Length
            ? WavePackReleaseDurations[waveIndex]
            : TimeSpan.Zero;

    /// <summary>
    /// Assigns a full room pack one affinity for this matching. The seeded plan is
    /// independent of summon RNG: a region has one primary affinity and, only when
    /// it owns a second room pack, one distinct secondary affinity.
    /// </summary>
    public static MonsterDefinition[] CreateDefinitionsForMatching(long matchingId)
    {
        var packs = Definitions
            .Where(definition => !definition.IsAmbientCorridor && !definition.IsReinforcement)
            .GroupBy(definition => definition.ClusterId)
            .Select(group => new { ClusterId = group.Key, Area = group.First().Area })
            .OrderBy(pack => pack.Area)
            .ThenBy(pack => pack.ClusterId)
            .ToList();
        var affinityByPack = new Dictionary<int, int>(packs.Count);
        var affinityOrder = AllAffinityItemIds.ToArray();
        var random = new Random(MatchSpawnData.GetDeterministicSeed(matchingId) ^ unchecked((int)0x4A9D_202));
        Shuffle(affinityOrder, random);

        int areaIndex = 0;
        foreach (var areaGroup in packs.GroupBy(pack => pack.Area).OrderBy(group => group.Key))
        {
            int packIndex = 0;
            foreach (var pack in areaGroup)
            {
                // Future multi-pack areas still remain bounded to two affinities.
                affinityByPack[pack.ClusterId] = affinityOrder[(areaIndex + Math.Min(packIndex++, 1)) % affinityOrder.Length];
            }
            areaIndex++;
        }

        // The reduced opening supply must still expose all three attack affinities.
        for (int hotspotIndex = 0; hotspotIndex < InitialHotspotAreas.Length; hotspotIndex++)
        {
            int packIndex = 0;
            foreach (var pack in packs.Where(pack => pack.Area == InitialHotspotAreas[hotspotIndex]))
            {
                affinityByPack[pack.ClusterId] =
                    affinityOrder[(hotspotIndex + Math.Min(packIndex++, 1)) % affinityOrder.Length];
            }
        }
        var primaryAffinityByArea = packs
            .GroupBy(pack => pack.Area)
            .ToDictionary(group => group.Key, group => affinityByPack[group.First().ClusterId]);

        return Definitions.Select(definition =>
        {
            if (definition.IsAmbientCorridor)
                return definition;

            if (definition.IsReinforcement)
            {
                return definition with
                {
                    RewardItemId = primaryAffinityByArea[definition.Area],
                    StartsActive = false
                };
            }

            return definition with
            {
                RewardItemId = affinityByPack[definition.ClusterId],
                StartsActive = definition.StartsActive &&
                               (!definition.IsCore || InitialHotspotAreas.Contains(definition.Area))
            };
        }).ToArray();
    }

    private static MonsterDefinition[] CreateDefinitions()
    {
        var definitions = new List<MonsterDefinition>(170);
        int id = EmotionAfterimageMonsterManager.FirstMonsterId;
        int nextPackId = 1;

        // #272 School2 이식 — 셀은 map_region_school2.csv rect에서 역산 (방 중심 부근 2~6점).
        // 시작방 8곳: 팩 1개(9마리)씩 — 개인 파밍 88초 분량.
        AddRoomPacks(AreaType.S2Classroom1, 1, [(103, 82), (107, 88)]);
        AddRoomPacks(AreaType.S2Classroom2, 2, [(170, 82), (174, 88)]);
        AddRoomPacks(AreaType.S2ExamRoom, 3, [(75, 53), (80, 60)]);
        AddRoomPacks(AreaType.S2BroadcastRoom, 4, [(75, -14), (80, -7)]);
        AddRoomPacks(AreaType.S2Storage, 5, [(103, -43), (107, -36)]);
        AddRoomPacks(AreaType.S2NurseOffice, 6, [(197, -14), (202, -7)]);
        AddRoomPacks(AreaType.S2AdminOffice1, 7, [(196, 53), (201, 60)]);
        AddRoomPacks(AreaType.S2AdminOffice2, 8, [(170, -43), (174, -36)]);

        // 합류 구역 4곳: 팩 2개 — 1:1 조우 무대의 공용 사냥터.
        AddRoomPacks(AreaType.S2Library1, 9, [(131, 72), (137, 78), (143, 84), (133, 86)]);
        AddRoomPacks(AreaType.S2Library2, 10, [(133, -39), (139, -33), (145, -27), (135, -25)]);
        AddRoomPacks(AreaType.S2Gym1, 11, [(77, 17), (83, 23), (89, 29), (79, 31)]);
        AddRoomPacks(AreaType.S2Gym2, 12, [(188, 17), (194, 23), (200, 29), (190, 31)]);

        // 운동장(풋살 코트, 병합 후 S2Corridor9 소속): 종반 수렴 무대 — 팩 2개.
        AddRoomPacks(AreaType.S2Corridor9, 13, [(128, 11), (134, 17), (140, 23), (146, 29), (150, 34), (132, 32)]);

        AddCorridorAnchors();
        return definitions.ToArray();

        void AddRoomPacks(AreaType area, int priority, IReadOnlyList<(int x, int y)> cells)
        {
            int packCount = (cells.Count + 2) / 3;
            for (int packOffset = 0; packOffset < packCount; packOffset++)
            {
                int packId = nextPackId++;
                var packCells = cells.Skip(packOffset * 3).Take(3).ToList();
                for (int memberIndex = 0; memberIndex < PackSize; memberIndex++)
                {
                    bool isCore = memberIndex == PackSize - 1;
                    // Preserve the authored cells as separate homes. Using only their
                    // average made all nine bodies appear to spawn from one point before
                    // the formation logic could spread them.
                    Vector3f memberHome = CellToWorld(Config.SWARM_MATCH_MAP,
                        packCells[memberIndex % packCells.Count].x,
                        packCells[memberIndex % packCells.Count].y);
                    definitions.Add(new MonsterDefinition(
                        id++, Config.SWARM_MATCH_MAP, area, memberHome,
                        MaxHealth: isCore ? 48 : 12,
                        AttackDamage: isCore ? 8 : 1,
                        AttackRange: 0.65f,
                        AttackIntervalSeconds: 1.5f,
                        // Replaced per matching by CreateDefinitionsForMatching.
                        RewardItemId: DefaultRoomAffinity,
                        IsCore: isCore,
                        SummonStoneReward: isCore ? 6 : 1,
                        MoveSpeed: 2.4f,
                        LeashRange: 5f,
                        AreaAliveLimit: packCount * PackSize,
                        StartsActive: packOffset == 0,
                        SpawnPriority: priority,
                        ClusterId: packId,
                        ClusterMemberIndex: memberIndex,
                        ClusterSize: PackSize,
                        FormationOffset: CreatePackFormationOffset(packId, memberIndex)));
                }
            }

            int reinforcementClusterId = nextPackId++;
            for (int memberIndex = 0; memberIndex < ReinforcementSlotsPerArea; memberIndex++)
            {
                var cell = cells[memberIndex % cells.Count];
                definitions.Add(new MonsterDefinition(
                    id++, Config.SWARM_MATCH_MAP, area, CellToWorld(Config.SWARM_MATCH_MAP, cell.x, cell.y),
                    MaxHealth: 12,
                    AttackDamage: 1,
                    AttackRange: 0.65f,
                    AttackIntervalSeconds: 1.5f,
                    RewardItemId: DefaultRoomAffinity,
                    IsCore: false,
                    // 증원도 보상 예산이 남아 있는 동안은 지급한다. 예산이 마르면 자동으로 0이 되므로
                    // 무한 리필이 무한 수입이 되지 않는다.
                    SummonStoneReward: 1,
                    MoveSpeed: 2.4f,
                    LeashRange: 5f,
                    AreaAliveLimit: ReinforcementSlotsPerArea,
                    StartsActive: false,
                    SpawnPriority: priority,
                    ClusterId: reinforcementClusterId,
                    ClusterMemberIndex: memberIndex,
                    ClusterSize: ReinforcementSlotsPerArea,
                    FormationOffset: CreateReinforcementFormationOffset(reinforcementClusterId, memberIndex),
                    IsReinforcement: true));
            }
        }

        void AddCorridorAnchors()
        {
            // #272 School2 (가운데 병합 후 재배치): 압박 앵커는 복도 1~8에 2점씩 — 압박 몹은
            // 타깃과 같은 구역이어야 유지되는데, 병합된 가운데는 복도가 아니라 캠프·보스가 맡는다.
            // These nodes never enter the closure refill economy.
            foreach (var anchor in new[]
                     {
                         (AreaType.S2Corridor1, 116, 78), (AreaType.S2Corridor1, 123, 80),
                         (AreaType.S2Corridor2, 152, 80), (AreaType.S2Corridor2, 161, 78),
                         (AreaType.S2Corridor3, 81, 37), (AreaType.S2Corridor3, 84, 44),
                         (AreaType.S2Corridor4, 82, 2), (AreaType.S2Corridor4, 85, 9),
                         (AreaType.S2Corridor5, 116, -32), (AreaType.S2Corridor5, 125, -34),
                         (AreaType.S2Corridor6, 154, -34), (AreaType.S2Corridor6, 161, -32),
                         (AreaType.S2Corridor7, 192, 37), (AreaType.S2Corridor7, 196, 44),
                         (AreaType.S2Corridor8, 193, 2), (AreaType.S2Corridor8, 197, 9)
                     })
            {
                int anchorId = nextPackId++;
                definitions.Add(new MonsterDefinition(
                    id++, Config.SWARM_MATCH_MAP, anchor.Item1,
                    CellToWorld(Config.SWARM_MATCH_MAP, anchor.Item2, anchor.Item3),
                    MaxHealth: 12,
                    AttackDamage: 1,
                    AttackRange: 0.65f,
                    AttackIntervalSeconds: 1.5f,
                    RewardItemId: 0,
                    IsCore: false,
                    SummonStoneReward: 1,
                    MoveSpeed: 2.4f,
                    LeashRange: 4f,
                    AreaAliveLimit: 6,
                    StartsActive: false,
                    SpawnPriority: int.MaxValue,
                    ClusterId: anchorId,
                    ClusterMemberIndex: 0,
                    ClusterSize: 1,
                    FormationOffset: new Vector3f(0f, 0f, 0f),
                    IsAmbientCorridor: true));
            }
        }
    }


    private static Vector3f CreateReinforcementFormationOffset(int clusterId, int memberIndex)
    {
        float angle = clusterId * 2.39996323f + MathF.Tau * memberIndex / ReinforcementSlotsPerArea;
        float radius = 1.45f + memberIndex % 2 * 0.25f;
        return new Vector3f(MathF.Cos(angle) * radius, MathF.Sin(angle) * radius, 0f);
    }

    private static Vector3f CreatePackFormationOffset(int packId, int memberIndex)
    {
        if (memberIndex == PackSize - 1)
            return new Vector3f(0f, 0f, 0f);

        bool markedNormal = memberIndex >= PackSize - 3;
        int ringIndex = markedNormal ? memberIndex - (PackSize - 3) : memberIndex;
        int ringCount = markedNormal ? 2 : 6;
        // Keep the initial pack legible even with the large MonsterT1 sprite.
        float radius = markedNormal ? 0.62f : 1.15f;
        float packRotation = (packId * 2.39996323f) % MathF.Tau;
        float angle = packRotation + MathF.Tau * ringIndex / ringCount;
        return new Vector3f(MathF.Cos(angle) * radius, MathF.Sin(angle) * radius, 0f);
    }


    private static void Shuffle<T>(IList<T> values, Random random)
    {
        for (int index = values.Count - 1; index > 0; index--)
        {
            int swapIndex = random.Next(index + 1);
            (values[index], values[swapIndex]) = (values[swapIndex], values[index]);
        }
    }
    private static Vector3f CellToWorld(MapId mapId, int cellX, int cellY) =>
        MapCoordinateConverter.CellToWorld(mapId, new Cell(cellX, cellY));
}
