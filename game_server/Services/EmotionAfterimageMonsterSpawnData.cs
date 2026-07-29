using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.services;

/// <summary>
/// Fixed room packs and corridor pressure anchors for the emotion-afterimage loop.
/// A room pack preserves the previous 144 HP / five-stone budget while making its
/// weak escort bodies readable as a small horde instead of more economy sources.
/// </summary>
internal static class EmotionAfterimageMonsterSpawnData
{
    private const int HopeT1 = 107000010;
    private const int ForgetT1 = 107000020;
    private const int DespairT1 = 107000030;
    private const int ComfortT1 = 107000040;
    private const int PackSize = 9;
    private const int DefaultRoomAffinity = HopeT1;
    private static readonly int[] AllAffinityItemIds = [HopeT1, ForgetT1, DespairT1, ComfortT1];

    public static readonly MonsterDefinition[] Definitions = CreateDefinitions();
    private static readonly int[] WavePackSpawnBudgets = [3, 3, 2, 2, 1];

    public static int GetWavePackSpawnBudget(int waveIndex) =>
        waveIndex >= 0 && waveIndex < WavePackSpawnBudgets.Length ? WavePackSpawnBudgets[waveIndex] : 0;

    /// <summary>
    /// Assigns a full room pack one affinity for this matching. The seeded plan is
    /// independent of summon RNG: a region has one primary affinity and, only when
    /// it owns a second room pack, one distinct secondary affinity.
    /// </summary>
    public static MonsterDefinition[] CreateDefinitionsForMatching(long matchingId)
    {
        var packs = Definitions
            .Where(definition => !definition.IsAmbientCorridor)
            .GroupBy(definition => definition.ClusterId)
            .Select(group => new { ClusterId = group.Key, Area = group.First().Area })
            .OrderBy(pack => pack.Area)
            .ThenBy(pack => pack.ClusterId)
            .ToList();
        var affinityByPack = new Dictionary<int, int>(packs.Count);
        var affinityOrder = AllAffinityItemIds.ToArray();
        var random = new Random(SurvivorRoyaleSpawnData.GetDeterministicSeed(matchingId) ^ unchecked((int)0x4A9D_202));
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

        return Definitions.Select(definition => definition.IsAmbientCorridor
            ? definition
            : definition with { RewardItemId = affinityByPack[definition.ClusterId] }).ToArray();
    }

    private static MonsterDefinition[] CreateDefinitions()
    {
        var definitions = new List<MonsterDefinition>(170);
        int id = EmotionAfterimageMonsterManager.FirstMonsterId;
        int nextPackId = 1;

        // The first Classroom4 pack retains 202001 so the placed MonsterT1 template
        // and the server's first stable identity continue to agree.
        AddRoomPacks(AreaType.Classroom4, 4, [(127, 95), (120, 91), (117, 99)]);
        AddRoomPacks(AreaType.ExamRoom, 1, [(113, 121), (118, 126)]);
        AddRoomPacks(AreaType.BroadcastRoom, 2, [(145, 121), (150, 126)]);
        AddRoomPacks(AreaType.Classroom2, 3, [(177, 121), (182, 126)]);
        AddRoomPacks(AreaType.Classroom3, 5, [(151, 94), (147, 89), (156, 99)]);

        AddRoomPacks(AreaType.Library, 6, [(92, 86), (96, 91), (90, 96), (97, 101)]);
        AddRoomPacks(AreaType.Gym, 7, [(180, 86), (188, 90), (180, 97), (190, 99)]);

        AddRoomPacks(AreaType.Storage, 8, [(87, 62), (93, 67)]);
        AddRoomPacks(AreaType.Junkyard, 9, [(103, 69), (112, 72)]);
        AddRoomPacks(AreaType.AdminOffice, 10, [(121, 63), (128, 69)]);
        AddRoomPacks(AreaType.StaffRoom, 11, [(149, 63), (160, 70)]);
        AddRoomPacks(AreaType.Junkyard2, 12, [(179, 72), (190, 75)]);
        AddRoomPacks(AreaType.Storage2, 13, [(201, 73), (208, 79)]);
        AddRoomPacks(AreaType.Ground, 14, [(92, 28), (104, 35), (118, 42), (130, 49), (112, 24), (136, 30)]);

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
                    Vector3f memberHome = CellToWorld(packCells[memberIndex % packCells.Count].x,
                        packCells[memberIndex % packCells.Count].y);
                    definitions.Add(new MonsterDefinition(
                        id++, MapId.School, area, memberHome,
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
        }

        void AddCorridorAnchors()
        {
            // Generic Corridor is the active School_New corridor area in map_region.csv.
            // These nodes never enter the closure refill economy.
            foreach (var cell in new[]
                     {
                         (140, 70), (174, 108), (112, 108), (145, 108),
                         (178, 73), (109, 85), (139, 93), (165, 85)
                     })
            {
                int anchorId = nextPackId++;
                definitions.Add(new MonsterDefinition(
                    id++, MapId.School, AreaType.Corridor, CellToWorld(cell.Item1, cell.Item2),
                    MaxHealth: 12,
                    AttackDamage: 1,
                    AttackRange: 0.65f,
                    AttackIntervalSeconds: 1.5f,
                    RewardItemId: 0,
                    IsCore: false,
                    SummonStoneReward: 1,
                    MoveSpeed: 2.4f,
                    LeashRange: 4f,
                    AreaAliveLimit: 3,
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
    private static Vector3f CellToWorld(int cellX, int cellY) =>
        new((cellX - cellY) / 2f, (cellX + cellY) / 4f, 0f);
}