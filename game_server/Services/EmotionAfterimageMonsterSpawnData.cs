using network.common;
using network.common.data.models;

namespace game_server.services;

/// <summary>
/// Fixed, visible spawn nodes for the emotion-afterimage loop.  The node owns
/// only terrain placement and regional capacity; a match owns occupancy.
/// </summary>
internal static class EmotionAfterimageMonsterSpawnData
{
    private const int HopeT1 = 107000010;
    private const int ForgetT1 = 107000020;
    private const int DespairT1 = 107000030;
    private const int ComfortT1 = 107000040;

    public static readonly MonsterDefinition[] Definitions = CreateDefinitions();
    private static readonly int[] WaveSpawnBudgets = [8, 7, 6, 5, 3];

    public static int GetWaveSpawnBudget(int waveIndex) =>
        waveIndex >= 0 && waveIndex < WaveSpawnBudgets.Length ? WaveSpawnBudgets[waveIndex] : 0;

    private static MonsterDefinition[] CreateDefinitions()
    {
        var definitions = new List<MonsterDefinition>(38);
        int id = EmotionAfterimageMonsterManager.FirstMonsterId;

        // The first Classroom4 point is the already-placed MonsterT1 template.
        // Keep it at 202001 so the scene template and server identity never diverge.
        AddNodes(AreaType.Classroom4, 2, 4, [(127, 95), (120, 91), (117, 99)]);
        AddNodes(AreaType.ExamRoom, 1, 1, [(113, 121), (118, 126)]);
        AddNodes(AreaType.BroadcastRoom, 1, 2, [(145, 121), (150, 126)]);
        AddNodes(AreaType.Classroom2, 1, 3, [(177, 121), (182, 126)]);
        AddNodes(AreaType.Classroom3, 2, 5, [(151, 94), (147, 89), (156, 99)]);

        AddNodes(AreaType.Library, 3, 6, [(92, 86), (96, 91), (90, 96), (97, 101)]);
        AddNodes(AreaType.Gym, 3, 7, [(180, 86), (188, 90), (180, 97), (190, 99)]);

        AddNodes(AreaType.Storage, 2, 8, [(87, 62), (93, 67)]);
        AddNodes(AreaType.Junkyard, 2, 9, [(103, 69), (112, 72)]);
        AddNodes(AreaType.AdminOffice, 2, 10, [(121, 63), (128, 69)]);

        AddNodes(AreaType.StaffRoom, 2, 11, [(149, 63), (160, 70)]);
        AddNodes(AreaType.Junkyard2, 2, 12, [(179, 72), (190, 75)]);
        AddNodes(AreaType.Storage2, 2, 13, [(201, 73), (208, 79)]);

        AddNodes(AreaType.Ground, 4, 14, [(92, 28), (104, 35), (118, 42), (130, 49), (112, 24), (136, 30)]);
        return definitions.ToArray();

        void AddNodes(AreaType area, int liveLimit, int priority, IReadOnlyList<(int x, int y)> cells)
        {
            for (int index = 0; index < cells.Count; index++)
            {
                var cell = cells[index];
                int monsterId = id++;
                definitions.Add(new MonsterDefinition(
                    monsterId, MapId.School, area, CellToWorld(cell.x, cell.y),
                    MaxHealth: 48, AttackDamage: 4, AttackRange: 0.65f, AttackIntervalSeconds: 1.5f,
                    RewardItemId: RewardFor(monsterId), MoveSpeed: 2.4f, LeashRange: 5f,
                    AreaAliveLimit: liveLimit, StartsActive: index < InitialOccupancy(area), SpawnPriority: priority));
            }
        }
    }

    private static int InitialOccupancy(AreaType area) => area switch
    {
        AreaType.Library or AreaType.Gym => 2,
        _ => 1
    };

    private static int RewardFor(int monsterId) => ((monsterId - EmotionAfterimageMonsterManager.FirstMonsterId) % 4) switch
    {
        0 => HopeT1,
        1 => ForgetT1,
        2 => DespairT1,
        _ => ComfortT1
    };

    private static Vector3f CellToWorld(int cellX, int cellY) =>
        new((cellX - cellY) / 2f, (cellX + cellY) / 4f, 0f);
}