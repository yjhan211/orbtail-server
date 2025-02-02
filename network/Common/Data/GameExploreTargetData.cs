// ReSharper disable All
#pragma warning disable CS8618 // 생성자를 종료할 때 null을 허용하지 않는 필드에 null이 아닌 값을 포함해야 합니다. null 허용으로 선언해 보세요.
#pragma warning disable CS8625 // Null 리터럴을 null을 허용하지 않는 참조 형식으로 변환할 수 없습니다.
#pragma warning disable CS8603 // 가능한 null 참조 반환입니다.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics.CodeAnalysis;
using Newtonsoft.Json;
using network.common.data.helpers;
using network.common.data.models;
using network.managers;

namespace network.common.data
{
    public static class GameExploreTargetData
    {
        private static readonly Dictionary<int, ExploreTargetInfoData> Infos = new();
        private static readonly Dictionary<MapId, List<ExploreTargetInfoData>> InfosByMap = new();

        public static void Initialize(List<CsvRow> csvData)
        {
            var infos = csvData.Select(ExploreTargetInfoData.CreateFromData);
            foreach (var info in infos)
            {
                Infos[info.Id] = info;
                if (!InfosByMap.TryGetValue(info.MapId, out var _))
                {
                    InfosByMap[info.MapId] = new List<ExploreTargetInfoData> { };
                }
                if (!InfosByMap[info.MapId].Contains(info))
                {
                    InfosByMap[info.MapId].Add(info);
                }
            }
        }

        public static ExploreTargetInfoData Get(int id)
        {
            if (!Infos.TryGetValue(id, out var info)) throw new KeyNotFoundException($"ExploreTarget {id} not found");
            return info;
        }

        public static List<ExploreTargetInfoData> GetAll()
        {
            return Infos.Values.ToList();
        }

        public static List<ExploreTargetInfoData> GetListByMap(MapId mapId)
        {
            if (!InfosByMap.TryGetValue(mapId, out var list))
            {
                return new List<ExploreTargetInfoData> { };
            }

            return list;
        }

        public static void Validate(LogManager logManager)
        {
            logManager.WriteDebugLog("=== GameExploreTargetData Validation ===");
            foreach (var (id, info) in Infos)
            {
                logManager.WriteDebugLog($"[{id}] {info.Name}");
            }
            logManager.WriteDebugLog("All validations passed successfully!");
        }
    }

    public class ExploreTargetInfoData
    {
        public int Id { get; private set; }
        public MapId MapId { get; private set; }
        public string Name { get; private set; }
        public List<int> RewardItemPool { get; private set; }
        public bool Reusable { get; private set; }
        public Cell Position { get; private set; }
        public string SpritePath { get; private set; }
        
        public static ExploreTargetInfoData CreateFromData(CsvRow row)
        {
            var posStr = row["position"]
                .Trim('"')
                .Trim('(', ')')
                .Split(',');
            
            return new ExploreTargetInfoData
            {
                Id = int.Parse(row["id"]),
                MapId = (MapId)Convert.ToInt32(row["map_id"]),
                Name = row["name"],
                RewardItemPool = JsonConvert.DeserializeObject<List<int>>(row["reward_item_pool"]) ?? new(),
                Reusable = int.Parse(row["id"]) == 0 ? false : true,
                Position = new Cell(int.Parse(posStr[0].Trim()), int.Parse(posStr[1].Trim())),
                SpritePath = row["sprite_path"],
            };
        }
    }
}