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
using UnityEngine;

namespace network.common.data
{
    public static class GameCraftData
    {
        private static readonly Dictionary<int, CraftInfoData> _infos = new();
        private static readonly Dictionary<int, List<CraftInfoData>> _infosByManual = new();

        public static void Initialize(List<CsvRow> csvData)
        {
            var infos = csvData.Select(CraftInfoData.CreateFromData);
            foreach (var info in infos)
            {
                _infos[info.Id] = info;
                if (!_infosByManual.TryGetValue(info.ManualId, out var _))
                {
                    _infosByManual[info.ManualId] = new List<CraftInfoData> { };
                }

                if (!_infosByManual[info.ManualId].Any(x => x.Id == info.Id))
                {
                    _infosByManual[info.ManualId].Add(info);
                }
            }
        }

        public static CraftInfoData Get(int id)
        {
            if (!_infos.TryGetValue(id, out var info)) throw new KeyNotFoundException($"CraftInfo {id} not found");
            return info;
        }

        public static List<CraftInfoData> GetListByManual(int manualId)
        {
            if (!_infosByManual.TryGetValue(manualId, out var list))
            {
                return new List<CraftInfoData> { };
            }

            return list;
        }

        public static void Validate(LogManager logManager)
        {
            LogManager.WriteDebugLog("=== GameCraftData Validation ===");
            foreach (var (id, info) in _infos)
            {
                LogManager.WriteDebugLog($"[{id}] {info.ManualId} | {info.TargetItem}");
            }
            LogManager.WriteDebugLog("All validations passed successfully!");
        }
    }

    public class CraftInfoData
    {
        public int Id { get; private set; }
        public int ManualId { get; private set; }
        public int TargetItem { get; private set; }
        public List<int> RequireMaterial { get; private set; }

        public static CraftInfoData CreateFromData(CsvRow row)
        {
            return new CraftInfoData
            {
                Id = int.Parse(row["id"]),
                ManualId = int.Parse(row["manual_id"]),
                TargetItem = int.Parse(row["target_item"]),
                RequireMaterial = QuestInfoData.ParseIntList(row["require_material"])
            };
        }
    }
}
