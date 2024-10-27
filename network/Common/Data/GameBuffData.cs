using System.Diagnostics.CodeAnalysis;
using network.common.helpers;
using network.managers;

namespace network.common.data;

public static class GameBuffData
{
    private static readonly Dictionary<int, BuffInfoData> Buffs = new();

    public static void Initialize(Dictionary<string, CsvRow> csvData)
    {
        var buffInfos = csvData.Values.Select(BuffInfoData.CreateFromData);
        foreach (var buffInfo in buffInfos) Buffs[buffInfo.Id] = buffInfo;
    }

    [SuppressMessage("ReSharper", "MemberCanBePrivate.Global")]
    public static BuffInfoData Get(int id)
    {
        if (!Buffs.TryGetValue(id, out var buff)) throw new KeyNotFoundException($"Buff {id} not found");
        return buff;
    }

    public static void Validate(LogManager logManager)
    {
        logManager.WriteDebugLog("=== GameBuffData Validation ===");
        foreach (var (id, buff) in Buffs)
        {
            logManager.WriteDebugLog($"Buff {id}:");
            logManager.WriteDebugLog($"  Type: {buff.Type}");
            logManager.WriteDebugLog($"  SubType: {buff.SubType}");

            switch (buff.Type)
            {
                case BuffType.INSTANT:
                case BuffType.PERIODIC:
                    break;

                default:
                    throw new InvalidDataException($"Buff {id} has invalid type: {buff.Type}");
            }

            switch (buff.SubType)
            {
                case BuffSubType.CONDITION_ADD:
                    break;

                default:
                    throw new InvalidDataException($"Buff {id} has invalid subType: {buff.SubType}");
            }


            logManager.WriteDebugLog("");
        }

        logManager.WriteDebugLog($"Total {Buffs.Count} buffs validated successfully!");
    }

    // 유틸리티 메서드
    public static bool IsPeriodicBuff(int buffId)
    {
        return Get(buffId).Type == BuffType.PERIODIC;
    }

    public static bool IsInstantBuff(int buffId)
    {
        return Get(buffId).Type == BuffType.INSTANT;
    }

    public static bool IsConditionBuff(int buffId)
    {
        return Get(buffId).SubType == BuffSubType.CONDITION_ADD;
    }
}

public class BuffInfoData
{
    public int Id { get; private init; }
    public BuffType Type { get; private init; }
    public BuffSubType SubType { get; private init; }

    public static BuffInfoData CreateFromData(CsvRow row)
    {
        return new BuffInfoData
        {
            Id = int.Parse(row["id"]),
            Type = (BuffType)int.Parse(row["type"]),
            SubType = (BuffSubType)int.Parse(row["sub_type"])
        };
    }
}