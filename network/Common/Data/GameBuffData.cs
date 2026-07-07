// ReSharper disable All
#pragma warning disable CS8618 // ?앹꽦?먮? 醫낅즺????null???덉슜?섏? ?딅뒗 ?꾨뱶??null???꾨땶 媛믪쓣 ?ы븿?댁빞 ?⑸땲?? null ?덉슜?쇰줈 ?좎뼵??蹂댁꽭??
#pragma warning disable CS8625 // Null 由ы꽣?댁쓣 null???덉슜?섏? ?딅뒗 李몄“ ?뺤떇?쇰줈 蹂?섑븷 ???놁뒿?덈떎.
#pragma warning disable CS8603 // 媛?ν븳 null 李몄“ 諛섑솚?낅땲??

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using network.common.data.helpers;
using network.managers;
using Newtonsoft.Json;

namespace network.common.data
{
    public static class GameBuffData
    {
        public const int PersonaSecretCollectorBuffId = 6;
        public const int PersonaCowardBuffId = 7;
        public const int PersonaGuardianAngelBuffId = 8;
        public const int PersonaPhysicalSolverBuffId = 9;
        public const int PersonaNocturnalBuffId = 10;
        public const int PersonaBuffValuePercent = 20;

        private static readonly Dictionary<int, BuffInfoData> _buffs = new();

        public static int GetPersonaBuffId(PersonaType persona) => persona switch
        {
            PersonaType.SecretCollector => PersonaSecretCollectorBuffId,
            PersonaType.Coward => PersonaCowardBuffId,
            PersonaType.GuardianAngel => PersonaGuardianAngelBuffId,
            PersonaType.PhysicalSolver => PersonaPhysicalSolverBuffId,
            PersonaType.Nocturnal => PersonaNocturnalBuffId,
            _ => 0
        };

        public static int GetDefaultPassiveBuffValuePercent(int buffId)
        {
            return buffId is >= PersonaSecretCollectorBuffId and <= PersonaNocturnalBuffId
                ? PersonaBuffValuePercent
                : 0;
        }

        public static void Initialize(List<CsvRow> csvData)
        {
            var buffInfos = csvData.Select(BuffInfoData.CreateFromData);
            foreach (var buffInfo in buffInfos) _buffs[buffInfo.Id] = buffInfo;
        }

        public static BuffInfoData Get(int id)
        {
            if (!_buffs.TryGetValue(id, out var buff)) throw new KeyNotFoundException($"Buff {id} not found");
            return buff;
        }

        public static IReadOnlyCollection<BuffInfoData> GetAll()
        {
            return _buffs.Values;
        }

        public static void Validate(LogManager logManager)
        {
            LogManager.WriteDebugLog("=== GameBuffData Validation ===");
            foreach (var (id, buff) in _buffs)
            {
                LogManager.WriteDebugLog($"Buff {id}:");
                LogManager.WriteDebugLog($"  Type: {buff.Type}");
                LogManager.WriteDebugLog($"  SubType: {buff.SubType}");
            }

            LogManager.WriteDebugLog($"Total {_buffs.Count} buffs validated successfully!");
        }

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
        public int Id { get; private set; }
        public BuffType Type { get; private set; }
        public BuffSubType SubType { get; private set; }
        public string Comment { get; private set; }

        public static BuffInfoData CreateFromData(CsvRow row)
        {
            return new BuffInfoData
            {
                Id = int.Parse(row["id"]),
                Type = (BuffType)int.Parse(row["type"]),
                SubType = (BuffSubType)int.Parse(row["sub_type"]),
                Comment = row["comment"],
            };
        }
    }
}
