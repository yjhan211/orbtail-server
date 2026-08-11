using System;
using System.Collections.Generic;
using System.Linq;
using network.common.data.models;

namespace network.common.data
{
    /// <summary>
    /// Shared color and tier rules for the three Survivor Royale orb lines added by #198.
    /// Legacy guardian orbs (107000003/004/006) intentionally remain outside this board rule.
    /// </summary>
    public enum SurvivorOrbColor
    {
        None = 0,
        Red = 1,
        Green = 2,
        Blue = 3,
        Recovery = 4
    }

    public enum SurvivorOrbAttackPattern
    {
        None = 0,
        HomingProjectile = 1,
        TargetArea = 2,
        AttackerArea = 3
    }

    public static class SurvivorOrbData
    {
        public const float RecoveryTickSeconds = 5f;
        public const float WindChargeSeconds = 2f;
        public const float WindMoveSpeedMultiplier = 1.2f;
        public const float WindAttackRangeMultiplier = 1.2f;
        public const float WindAttackIntervalMultiplier = 0.85f;
        public const float WindBaseDamageMultiplier = 0.5f;
        public const float WindBaseAttackIntervalMultiplier = 0.5f;
        public const float WindProjectileSpeedMultiplier = 1.25f;
        public const float SunMarkLifetimeSeconds = 3f;
        public const int SunMarkTriggerCount = 3;
        public const float SunBurstDamageMultiplier = 0.75f;
        public const float SunBurstRadius = 2.4f;
        public const float SunFiveBurstRadiusMultiplier = 1.5f;
        public const int SunFiveBurstMaxTargets = 2;
        public const float WaveHitWindowSeconds = 1.5f;
        public const float WaveCounterCooldownSeconds = 6f;
        public const float WaveCounterDamageMultiplier = 1.25f;
        public const float WaveSlowSeconds = 1.5f;
        public const float WaveSlowMoveSpeedMultiplier = 0.65f;
        public const float WaveBaseAttackIntervalMultiplier = 1.25f;
        // The current room-horde pace needs each orb to fire twice as often.
        public const float OrbAttackIntervalMultiplier = 0.5f;
        // A Wave orb detonates at its primary target and damages every valid target in this radius.
        public const float WaveSplashRadius = 1.8f;
        public const float WaveTierTwoSplashRadius = 2.2f;
        public const float WaveTierThreeSplashRadius = 2.6f;
        public const float WindPulseRadius = 2.4f;
        public const float WindTierTwoPulseRadius = 2.8f;
        public const float WindTierThreePulseRadius = 3.2f;
        public const float PveAdvantageDamageMultiplier = 1.5f;
        public const float PveNeutralDamageMultiplier = 1f;
        public const float PveDisadvantageDamageMultiplier = 0.5f;

        // PvP attacks are fired at the target's launch-time position. These shared
        // timings keep the server impact check and Unity projectile presentation aligned.
        public const float HopeProjectileSpeed = 6f;
        public const float ForgetProjectileSpeed = 9f;
        public const float DespairImpactDelaySeconds = 0.8f;
        public const float MinimumProjectileImpactDelaySeconds = 0.45f;
        public const float MaximumProjectileImpactDelaySeconds = 1.4f;
        public const float ProjectileTargetBodyRadius = 0.4f;

        // #219 M2: 색 = 스탯 축 (SB 유닛 선택의 압축). 태양(빨강)=공격력, 바람(초록)=이속,
        // 파도(파랑)=사거리. 매 개봉의 색 선택이 빌드 결정이 된다.
        public const float SunAttackBonusPerOrb = 0.15f;
        // 바람 재정의 (#222 M4): 이속 → 공격속도. 부츠(이동 소모품)와 컨셉이 겹쳤고, 상시
        // 이동 게임이라 이속 체감도 낮았다. 공속은 연사 리듬으로 즉시 읽힌다 — 태양(발당
        // 무게)과 다른 체감 축. 기본 연사(RapidFireScale)를 늦춘 만큼 바람이 그 이상을 되돌린다.
        public const float WindAttackSpeedBonusPerOrb = 0.08f;
        public const float WindAttackSpeedBonusCap = 0.60f;
        public const float WaveRangeBonusPerOrb = 0.4f;
        public const float WaveRangeBonusCap = 3f;

        /// <summary>
        ///     스탯 티어 가중(1/1.75/4): 3머지는 슬롯·개봉비를 돌려주는 대신 스탯 합이
        ///     약간 손해 — 전문화(머지) vs 분산(보유)의 트레이드가 SB 융합 문법이다.
        /// </summary>
        public static float GetSwarmStatTierWeight(int tier) =>
            tier >= 3 ? 4f : tier == 2 ? 1.75f : 1f;

        // 상자 시간 등급 (#222 M3, SB 커먼→레어→에픽): 개전 후 경과초가 드래프트 오브 티어를
        // 정한다. 4분 매치 3등분 — 폐쇄 웨이브(80/160초)와 같은 박자로 판의 살림이 굵어진다.
        public const int DraftTierTwoAtSeconds = 80;
        public const int DraftTierThreeAtSeconds = 160;

        public static int GetDraftTierByElapsed(double elapsedSeconds) =>
            elapsedSeconds >= DraftTierThreeAtSeconds ? 3 :
            elapsedSeconds >= DraftTierTwoAtSeconds ? 2 : 1;

        /// <summary>색 T1 아이템 ID에 시간 등급 티어를 적용한다 (색 베이스 +0/+1/+2).</summary>
        public static int ApplyDraftTier(int tierOneItemId, int tier)
        {
            if (!TryGetColorAndTier(tierOneItemId, out _, out int baseTier) || baseTier != 1)
                return tierOneItemId;

            return tierOneItemId + Math.Clamp(tier, 1, 3) - 1;
        }

        /// <summary>
        ///     유닛 낱개 체력 (SB 클론): 티어별 오브 HP. 서버 정산(GameServer.SwarmArena)과
        ///     클라 스쿼드 체력바 미러가 같은 값을 읽는다.
        ///     2026-08-09: 몹 피통 하향(해골 1방 체제)과 함께 2배 상향(12/28/60 → 24/56/120) —
        ///     몹은 빨리 녹고 오브는 오래 버텨야 교전이 즉사전이 아니라 소모전이 된다.
        /// </summary>
        public static int GetSquadOrbMaxHp(int tier) => tier >= 3 ? 120 : tier == 2 ? 56 : 24;

        /// <summary>궤도 전체의 색 스탯 합산 — 서버 판정과 클라 표시(링)가 같은 값을 읽는다.</summary>
        public static (float AttackMultiplier, float AttackSpeedMultiplier, float RangeBonus)
            GetSwarmColorStats(IEnumerable<InGameItemInfo> items)
        {
            float sun = 0f;
            float wind = 0f;
            float wave = 0f;
            foreach (var item in items)
            {
                if (item == null || item.Count <= 0) continue;
                if (!TryGetColorAndTier(item.ItemId, out SurvivorOrbColor color, out int tier)) continue;
                float weight = GetSwarmStatTierWeight(tier) * item.Count;
                if (color == SurvivorOrbColor.Red) sun += weight;
                else if (color == SurvivorOrbColor.Green) wind += weight;
                else if (color == SurvivorOrbColor.Blue) wave += weight;
            }

            return (
                1f + sun * SunAttackBonusPerOrb,
                1f + Math.Min(WindAttackSpeedBonusCap, wind * WindAttackSpeedBonusPerOrb),
                Math.Min(WaveRangeBonusCap, wave * WaveRangeBonusPerOrb));
        }

        private static readonly SurvivorOrbColor[] EvolutionColors =
            new[] { SurvivorOrbColor.Red, SurvivorOrbColor.Green, SurvivorOrbColor.Blue };

        public static bool TryGetColorAndTier(int itemId, out SurvivorOrbColor color, out int tier)
        {
            switch (itemId)
            {
                case 107000010: color = SurvivorOrbColor.Red; tier = 1; return true;
                case 107000011: color = SurvivorOrbColor.Red; tier = 2; return true;
                case 107000012: color = SurvivorOrbColor.Red; tier = 3; return true;
                case 107000020: color = SurvivorOrbColor.Green; tier = 1; return true;
                case 107000021: color = SurvivorOrbColor.Green; tier = 2; return true;
                case 107000022: color = SurvivorOrbColor.Green; tier = 3; return true;
                case 107000030: color = SurvivorOrbColor.Blue; tier = 1; return true;
                case 107000031: color = SurvivorOrbColor.Blue; tier = 2; return true;
                case 107000032: color = SurvivorOrbColor.Blue; tier = 3; return true;
                default: color = SurvivorOrbColor.None; tier = 0; return false;
            }
        }

        public static bool IsSurvivorOrb(int itemId) => TryGetColorAndTier(itemId, out _, out _);

        public static SurvivorOrbAttackPattern GetAttackPattern(int itemId)
        {
            // 색 = 무기 동사 (#226): 태양 = 전역 유도 미사일, 바람 = 공명·런지(투사체 없음),
            // 파도 = 미사일 없음(물폭탄은 서버 별도 주기) — None이면 클라 투사체도 안 뜬다.
            if (!TryGetColorAndTier(itemId, out SurvivorOrbColor color, out _))
                return SurvivorOrbAttackPattern.None;

            return color switch
            {
                SurvivorOrbColor.Red => SurvivorOrbAttackPattern.HomingProjectile,
                SurvivorOrbColor.Green => SurvivorOrbAttackPattern.AttackerArea,
                SurvivorOrbColor.Blue => SurvivorOrbAttackPattern.None,
                _ => SurvivorOrbAttackPattern.None
            };
        }

        public static float GetWindPulseRadius(int itemId)
        {
            if (!TryGetColorAndTier(itemId, out SurvivorOrbColor color, out int tier) ||
                color != SurvivorOrbColor.Green)
            {
                return WindPulseRadius;
            }

            return tier switch
            {
                >= 3 => WindTierThreePulseRadius,
                2 => WindTierTwoPulseRadius,
                _ => WindPulseRadius
            };
        }

        public static float GetPvpProjectileImpactDelaySeconds(int itemId, float distance)
        {
            // 공격 문법 통일: 전 색 같은 미사일 속도. 색 분기(파도 고정 딜레이·바람 고속탄) 퇴역.
            return Math.Clamp(
                Math.Max(0f, distance) / HopeProjectileSpeed,
                MinimumProjectileImpactDelaySeconds,
                MaximumProjectileImpactDelaySeconds);
        }

        public static float GetPvpProjectileHitRadius(int itemId, float projectileWidth)
        {
            return Math.Max(0.55f, Math.Max(0f, projectileWidth) + ProjectileTargetBodyRadius);
        }

        // #219 M2 공격 문법 통일: 색별 공속·데미지 차이 퇴역 — 색은 시각과 스탯 버프만.
        public static float GetBaseAttackIntervalMultiplier(SurvivorOrbColor color) => 1f;

        private static float LegacyBaseAttackIntervalMultiplier(SurvivorOrbColor color) => color switch
        {
            SurvivorOrbColor.Green => WindBaseAttackIntervalMultiplier,
            SurvivorOrbColor.Blue => WaveBaseAttackIntervalMultiplier,
            _ => 1f
        };

        public static int GetBaseAttackDamage(int baseDamage, SurvivorOrbColor color)
        {
            // 통일: 바람 데미지 반감 퇴역 — 전 색 동일 기본 데미지.
            return Math.Max(0, baseDamage);
        }

        public static float GetAttackIntervalMultiplier(int itemId) => itemId switch
        {
            107000003 or 107000004 or 107000006 => OrbAttackIntervalMultiplier,
            _ when IsSurvivorOrb(itemId) => OrbAttackIntervalMultiplier,
            _ => 1f
        };

        public static float GetPveDamageMultiplier(int attackerItemId, int monsterRewardItemId)
        {
            if (!TryGetColorAndTier(attackerItemId, out SurvivorOrbColor attackerColor, out _) ||
                !TryGetColorAndTier(monsterRewardItemId, out SurvivorOrbColor targetColor, out _))
                return PveNeutralDamageMultiplier;

            return GetPveDamageMultiplier(attackerColor, targetColor);
        }

        public static float GetPveDamageMultiplier(SurvivorOrbColor attackerColor, int monsterRewardItemId)
        {
            if (!TryGetColorAndTier(monsterRewardItemId, out SurvivorOrbColor targetColor, out _))
                return PveNeutralDamageMultiplier;

            return GetPveDamageMultiplier(attackerColor, targetColor);
        }

        public static float GetPveDamageMultiplier(SurvivorOrbColor attackerColor, SurvivorOrbColor targetColor)
        {
            // 통일: 색 상성(1.5/0.5) 퇴역 — 클론 비목표(상성 금지). 항상 중립 배율.
            return PveNeutralDamageMultiplier;
        }

        /// <summary>
        /// Resolves the board-wide PvE affinity only when one attack colour owns a
        /// strict majority of every occupied orb slot. Tiers do not affect resonance.
        /// Recovery orbs count as occupied slots but never become an attack affinity.
        /// </summary>
        public static bool TryGetDominantPveColor(
            IEnumerable<int> boardItemIds,
            out SurvivorOrbColor dominantColor)
        {
            if (boardItemIds == null)
                throw new ArgumentNullException(nameof(boardItemIds));

            var orbCounts = new Dictionary<SurvivorOrbColor, int>
            {
                [SurvivorOrbColor.Red] = 0,
                [SurvivorOrbColor.Green] = 0,
                [SurvivorOrbColor.Blue] = 0
            };
            int occupiedOrbCount = 0;

            foreach (int itemId in boardItemIds)
            {
                if (IsRecoveryOrb(itemId))
                {
                    occupiedOrbCount++;
                    continue;
                }

                if (!TryGetColorAndTier(itemId, out SurvivorOrbColor color, out _) ||
                    !orbCounts.ContainsKey(color))
                {
                    continue;
                }

                occupiedOrbCount++;
                orbCounts[color]++;
            }

            if (occupiedOrbCount < 2)
            {
                dominantColor = SurvivorOrbColor.None;
                return false;
            }

            var majority = orbCounts
                .Where(pair => pair.Value * 2 > occupiedOrbCount)
                .Select(pair => pair.Key)
                .ToArray();
            if (majority.Length == 1)
            {
                dominantColor = majority[0];
                return true;
            }

            dominantColor = SurvivorOrbColor.None;
            return false;
        }

        public static int CalculatePveDamage(
            int attackerItemId,
            int monsterRewardItemId,
            int baseDamage,
            float hitDamageMultiplier = 1f)
        {
            if (baseDamage <= 0 || hitDamageMultiplier <= 0f)
                return 0;

            float damage = baseDamage * hitDamageMultiplier *
                           GetPveDamageMultiplier(attackerItemId, monsterRewardItemId);
            return Math.Max(1, (int)Math.Ceiling(damage));
        }

        public static int CalculatePveDamage(
            SurvivorOrbColor attackerColor,
            int monsterRewardItemId,
            int baseDamage,
            float hitDamageMultiplier = 1f)
        {
            if (baseDamage <= 0 || hitDamageMultiplier <= 0f)
                return 0;

            float damage = baseDamage * hitDamageMultiplier *
                           GetPveDamageMultiplier(attackerColor, monsterRewardItemId);
            return Math.Max(1, (int)Math.Ceiling(damage));
        }
        public static bool TryGetRecoveryTier(int itemId, out int tier)
        {
            tier = itemId switch
            {
                107000040 => 1,
                107000041 => 2,
                107000042 => 3,
                _ => 0
            };
            return tier > 0;
        }

        public static bool IsRecoveryOrb(int itemId) => TryGetRecoveryTier(itemId, out _);

        public static int GetRecoveryAmount(int itemId) =>
            TryGetRecoveryTier(itemId, out int tier)
                ? tier switch
                {
                    1 => 5,
                    2 => 10,
                    3 => 20,
                    _ => 0
                }
                : 0;

        /// <summary>
        /// Validates a P1 merge and randomly evolves its colour. The server calls this only after
        /// receiving the two inputs; clients must not predict the result.
        /// </summary>
        public static bool CanMerge(int inputA, int inputB)
        {
            if (TryGetRecoveryTier(inputA, out int recoveryTierA) &&
                TryGetRecoveryTier(inputB, out int recoveryTierB))
            {
                return recoveryTierA == recoveryTierB && recoveryTierA < 3;
            }

            return TryGetColorAndTier(inputA, out SurvivorOrbColor colorA, out int tierA) &&
                   TryGetColorAndTier(inputB, out SurvivorOrbColor colorB, out int tierB) &&
                   colorA == colorB && tierA == tierB && tierA < 3;
        }

        public static bool TryGetRandomMergeOutput(int inputA, int inputB, Random random, out int outputItemId)
        {
            if (random == null) throw new ArgumentNullException(nameof(random));
            outputItemId = 0;
            if (!CanMerge(inputA, inputB))
                return false;

            if (TryGetRecoveryTier(inputA, out int recoveryTier))
            {
                outputItemId = recoveryTier switch
                {
                    1 => 107000041,
                    2 => 107000042,
                    _ => 0
                };
                return outputItemId > 0;
            }

            if (!TryGetColorAndTier(inputA, out _, out int tier))
                return false;

            return TryGetItemId(EvolutionColors[random.Next(EvolutionColors.Length)], tier + 1, out outputItemId);
        }

        public static bool TryGetItemId(SurvivorOrbColor color, int tier, out int itemId)
        {
            itemId = (color, tier) switch
            {
                (SurvivorOrbColor.Red, 1) => 107000010,
                (SurvivorOrbColor.Red, 2) => 107000011,
                (SurvivorOrbColor.Red, 3) => 107000012,
                (SurvivorOrbColor.Green, 1) => 107000020,
                (SurvivorOrbColor.Green, 2) => 107000021,
                (SurvivorOrbColor.Green, 3) => 107000022,
                (SurvivorOrbColor.Blue, 1) => 107000030,
                (SurvivorOrbColor.Blue, 2) => 107000031,
                (SurvivorOrbColor.Blue, 3) => 107000032,
                _ => 0
            };
            return itemId > 0;
        }

        public static int GetSunResonanceStage(int sunCount)
        {
            if (sunCount >= 5) return 5;
            if (sunCount >= 3) return 3;
            return sunCount >= 1 ? 1 : 0;
        }
        public static bool TryGetActivePair(
            IEnumerable<int> boardItemIds,
            out SurvivorOrbColor color,
            out int pairTier)
        {
            if (boardItemIds == null)
                throw new ArgumentNullException(nameof(boardItemIds));

            var itemIds = boardItemIds.ToList();
            foreach (SurvivorOrbColor candidateColor in EvolutionColors)
            {
                if (!HasActivePair(itemIds, candidateColor, out pairTier))
                    continue;

                color = candidateColor;
                return true;
            }

            color = SurvivorOrbColor.None;
            pairTier = 0;
            return false;
        }

        public static bool HasActivePair(
            IEnumerable<int> boardItemIds,
            SurvivorOrbColor targetColor,
            out int pairTier)
        {
            if (boardItemIds == null)
                throw new ArgumentNullException(nameof(boardItemIds));

            int matchingCount = 0;
            pairTier = 0;
            foreach (int itemId in boardItemIds)
            {
                if (!TryGetColorAndTier(itemId, out SurvivorOrbColor color, out int tier) ||
                    color != targetColor)
                    continue;

                matchingCount++;
                pairTier = Math.Max(pairTier, tier);
            }

            if (matchingCount >= 2) return true;
            pairTier = 0;
            return false;
        }

        /// <summary>
        /// Resonance is active when the equipped orb has at least one other orb of the same colour.
        /// The equipped item must be excluded from <paramref name="otherBoardItemIds"/> by the caller.
        /// <paramref name="pairTier"/> is retained for legacy callers and reports the highest support tier.
        /// </summary>
        public static bool TryGetActivePair(
            int equippedItemId,
            IEnumerable<int> otherBoardItemIds,
            out SurvivorOrbColor color,
            out int pairTier)
        {
            if (otherBoardItemIds == null)
                throw new ArgumentNullException(nameof(otherBoardItemIds));

            pairTier = 0;
            if (!TryGetColorAndTier(equippedItemId, out color, out _))
                return false;

            foreach (int itemId in otherBoardItemIds)
            {
                if (!TryGetColorAndTier(itemId, out SurvivorOrbColor candidateColor, out int candidateTier) ||
                    candidateColor != color)
                {
                    continue;
                }

                pairTier = Math.Max(pairTier, candidateTier);
            }

            return pairTier > 0;
        }
    }
}
