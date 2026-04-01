// ReSharper disable All
#pragma warning disable CS8618 // 생성자를 종료할 때 null을 허용하지 않는 필드에 null이 아닌 값을 포함해야 합니다. null 허용으로 선언해 보세요.
#pragma warning disable CS8625 // Null 리터럴을 null을 허용하지 않는 참조 형식으로 변환할 수 없습니다.
#pragma warning disable CS8603 // 가능한 null 참조 반환입니다.

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
        private static readonly Dictionary<int, BuffInfoData> _buffs = new();

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
