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

        // #229: 태양과 바람은 같은 유도탄을 사용하고 보드 패시브만 다르다. 패시브는
        // 티어 가중치가 아니라 살아 있는 오브 개수만 본다. 티어는 해당 오브의 공격만 강화한다.
        public const float SunFirstAttackBonus = 0.15f;
        public const float SunAdditionalAttackBonus = 0.05f;
        public const float SunAttackBonusCap = 0.40f;
        public const float WindFirstMoveSpeedBonus = 0.06f;
        public const float WindAdditionalMoveSpeedBonus = 0.02f;
        public const float WindMoveSpeedBonusCap = 0.14f;

        // #229 P0 PvE 실효값. 태양·바람 유도탄과 파도 물폭탄이 같은 티어 피해표를 쓴다.
        public static int GetSwarmPveAttackDamage(int itemId)
        {
            if (!TryGetColorAndTier(itemId, out _, out int tier))
                return 0;

            return tier >= 3 ? 30 : tier == 2 ? 21 : 12;
        }

        public static float GetSwarmPveAttackIntervalSeconds(int itemId)
        {
            if (!TryGetColorAndTier(itemId, out _, out int tier))
                return 0f;

            return tier >= 3 ? 0.7f : tier == 2 ? 1f : 1.4f;
        }

        public static float GetSwarmWaveBombRadius(int itemId)
        {
            if (!TryGetColorAndTier(itemId, out SurvivorOrbColor color, out int tier) ||
                color != SurvivorOrbColor.Blue)
            {
                return 0f;
            }

            return tier >= 3 ? WaveTierThreeSplashRadius :
                tier == 2 ? WaveTierTwoSplashRadius : WaveSplashRadius;
        }

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

        /// <summary>
        ///     티어 크기 배율 — 티어 = 크기가 오브열의 문장 부호다. 클라 슬롯 스케일과
        ///     열 간격이 같은 표를 읽어야 "커진 만큼 벌어진다"가 성립한다 (#227).
        /// </summary>
        public static float GetSwarmOrbTierScale(int tier) =>
            tier >= 3 ? 1.7f : tier == 2 ? 1.35f : 1f;

        /// <summary>
        ///     열에서 ordinal번째 오브까지의 경로 거리 (#227): 간격이 이웃 두 오브의 크기
        ///     평균에 비례한다 — 고정 간격에서는 티어가 오를수록 오브가 서로 파고들었다.
        ///     서버 판정(발사 원점·절단 좌표)과 클라 표시가 이 함수 하나만 쓴다.
        /// </summary>
        public static float GetSwarmTrailDistance(IReadOnlyList<int> orderedTiers, int ordinal)
        {
            float distance = Config.SWARM_ORB_TRAIL_FIRST_OFFSET * TierScaleAt(orderedTiers, 0);
            for (int index = 1; index <= ordinal; index++)
            {
                float previous = TierScaleAt(orderedTiers, index - 1);
                float current = TierScaleAt(orderedTiers, index);
                distance += Config.SWARM_ORB_TRAIL_SPACING * (previous + current) * 0.5f;
            }

            return distance;
        }

        /// <summary>목록이 짧거나 비어도 T1로 떨어져 서버·클라가 같은 값을 낸다.</summary>
        private static float TierScaleAt(IReadOnlyList<int> orderedTiers, int index) =>
            orderedTiers != null && index >= 0 && index < orderedTiers.Count
                ? GetSwarmOrbTierScale(orderedTiers[index])
                : 1f;

        public static int CountLivingOrbs(
            IEnumerable<InGameItemInfo> items,
            SurvivorOrbColor color)
        {
            if (items == null)
                return 0;

            int count = 0;
            foreach (var item in items)
            {
                if (item == null || item.Count <= 0 ||
                    !TryGetColorAndTier(item.ItemId, out SurvivorOrbColor itemColor, out _) ||
                    itemColor != color)
                {
                    continue;
                }

                count += item.Count;
            }

            return count;
        }

        public static float GetSunPveAttackMultiplier(IEnumerable<InGameItemInfo> items)
        {
            int count = CountLivingOrbs(items, SurvivorOrbColor.Red);
            if (count <= 0)
                return 1f;

            float bonus = SunFirstAttackBonus + (count - 1) * SunAdditionalAttackBonus;
            return 1f + Math.Min(SunAttackBonusCap, bonus);
        }

        public static float GetWindMoveSpeedMultiplier(IEnumerable<InGameItemInfo> items)
        {
            int count = CountLivingOrbs(items, SurvivorOrbColor.Green);
            if (count <= 0)
                return 1f;

            float bonus = WindFirstMoveSpeedBonus + (count - 1) * WindAdditionalMoveSpeedBonus;
            return 1f + Math.Min(WindMoveSpeedBonusCap, bonus);
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
                // 유도탄 복귀 (2026-08-12 플레이 판정): 직선탄 회피 실험은 상시 이동 게임에서
                // 상시 회피 = 유령 사격이 됐다(명중 전멸·루즈). 착탄 확정 유도탄으로 원복 —
                // 위치 판단 축은 절단·물폭탄·사거리가 맡는다. 클라는 이 패턴이면 표적을 추적한다.
                SurvivorOrbColor.Red => SurvivorOrbAttackPattern.HomingProjectile,
                SurvivorOrbColor.Green => SurvivorOrbAttackPattern.HomingProjectile,
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
