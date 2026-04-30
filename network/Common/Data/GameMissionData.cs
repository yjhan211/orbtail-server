// ReSharper disable All
#pragma warning disable CS8618

using System;
using System.Collections.Generic;
using System.Linq;
using network.common.data.helpers;
using network.managers;

namespace network.common.data
{
    /// <summary>
    ///     v0.2.0 — 부품 결합 시스템(이슈 #85). 직책별 7 부품(소재 4 + 중간재 2 + 최종 1) 정의.
    ///     기존 단계 기반 미션은 폐기. 부품 회수/결합으로 race 진행.
    /// </summary>
    public static class GameMissionData
    {
        // job_title → part_id 순 정렬된 부품 목록
        private static readonly Dictionary<short, List<MissionPartData>> _partsByJob = new();

        // part_id → MissionPartData 직접 조회
        private static readonly Dictionary<int, MissionPartData> _partsById = new();

        public static void Initialize(List<CsvRow> data)
        {
            _partsByJob.Clear();
            _partsById.Clear();

            foreach (var row in data)
            {
                short jobTitle = short.Parse(row["job_title"]);
                var part = new MissionPartData
                {
                    JobTitle = jobTitle,
                    PartId = int.Parse(row["part_id"]),
                    PartNameKr = row["part_name_kr"],
                    PartTier = (PartTier)int.Parse(row["part_tier"]),
                    TargetArea = int.Parse(row["target_area"]),
                    TargetObjectType = int.Parse(row["target_object_type"]),
                    StaminaReward = int.Parse(row["stamina_reward"]),
                    PrerequisiteShareGroup = int.Parse(row["prerequisite_share_group"]),
                    CombineProgressSeconds = int.Parse(row["combine_progress_seconds"])
                };

                if (!_partsByJob.ContainsKey(jobTitle))
                    _partsByJob[jobTitle] = new List<MissionPartData>();
                _partsByJob[jobTitle].Add(part);
                _partsById[part.PartId] = part;
            }

            foreach (var parts in _partsByJob.Values)
                parts.Sort((a, b) => a.PartId.CompareTo(b.PartId));
        }

        /// <summary>
        ///     해당 직책의 모든 부품(소재 + 중간재 + 최종) 반환
        /// </summary>
        public static List<MissionPartData> GetParts(short jobTitle) =>
            _partsByJob.GetValueOrDefault(jobTitle) ?? new List<MissionPartData>();

        /// <summary>
        ///     해당 직책의 소재(Tier 0)만 반환 — 회수 가능 부품
        /// </summary>
        public static List<MissionPartData> GetMaterials(short jobTitle) =>
            GetParts(jobTitle).Where(p => p.PartTier == PartTier.Material).ToList();

        /// <summary>
        ///     해당 직책의 최종 부품(Tier 2) 반환 — race 완주 trigger
        /// </summary>
        public static MissionPartData GetFinalPart(short jobTitle) =>
            GetParts(jobTitle).FirstOrDefault(p => p.PartTier == PartTier.Final);

        /// <summary>
        ///     part_id로 부품 직접 조회
        /// </summary>
        public static MissionPartData GetPart(int partId) =>
            _partsById.GetValueOrDefault(partId);

        /// <summary>
        ///     해당 직책의 총 부품 수 (소재 4 + 중간재 2 + 최종 1 = 7)
        /// </summary>
        public static int GetTotalParts(short jobTitle) => GetParts(jobTitle).Count;

        /// <summary>
        ///     호환 stub — Phase 3b 마이그레이션 전 임시. 단계 기반 호출자가 빌드 통과하도록.
        /// </summary>
        public static MissionPartData GetStep(short jobTitle, int stepOrder)
        {
            var parts = GetParts(jobTitle);
            return stepOrder >= 1 && stepOrder <= parts.Count ? parts[stepOrder - 1] : null;
        }

        public static void Validate(managers.LogManager logger)
        {
            if (_partsByJob.Count == 0)
                throw new InvalidOperationException("미션 부품 데이터가 로드되지 않았습니다");

            foreach (var (jobTitle, parts) in _partsByJob)
            {
                if (parts.Count != 7)
                    LogManager.WriteInfoLog($"[GameMissionData] 직책 {jobTitle} 부품 수 비정상: {parts.Count}/7");
            }
        }
    }

    public enum PartTier
    {
        Material = 0,    // 소재 — 회수 대상
        Intermediate = 1, // 중간재 — 결합 결과
        Final = 2,       // 최종 — race 완주 trigger
    }

    public class MissionPartData
    {
        public short JobTitle { get; set; }
        public int PartId { get; set; }              // 예: 101 (BR_M1)
        public string PartNameKr { get; set; }       // 예: "손상된 마이크 헤드"
        public PartTier PartTier { get; set; }
        public int TargetArea { get; set; }          // 소재만 의미 있음 (중간재/최종은 0)
        public int TargetObjectType { get; set; }    // 소재만 의미 있음 (Cabinet/Locker 등)
        public int StaminaReward { get; set; }       // 소재 회수 시 +12, 중간재 결합 시 +20, 최종 0
        public int PrerequisiteShareGroup { get; set; } // 0=선행 없음, 1+=선행 그룹 ID
        public int CombineProgressSeconds { get; set; } // 결합 progress (소재=0, 결합 부품=5)

        // ===== 호환 프로퍼티 (Phase 3b 마이그레이션 전 임시) =====

        /// <summary>호환 stub — 단계 기반 호출자용. Phase 3b에서 폐기.</summary>
        public int StepOrder => PartId % 100;

        /// <summary>호환 stub — Phase 3b에서 폐기.</summary>
        public int TargetInteractId => 0;

        /// <summary>호환 stub — Phase 3b에서 폐기.</summary>
        public int TargetActionId => 0;

        /// <summary>호환 stub — Phase 3b에서 폐기.</summary>
        public string TraceDescription => "";
    }
}
