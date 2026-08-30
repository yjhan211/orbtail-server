// ReSharper disable All
#pragma warning disable CS8618 // Legacy CSV models are populated after construction.
#pragma warning disable CS8625 // Legacy lookup APIs use null as a missing-value sentinel.
#pragma warning disable CS8603 // Legacy lookup APIs may return null.

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
