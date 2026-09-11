using game_server.players;

using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.matches.combat;
/// <summary>검증된 공간 정보와 인벤토리로 전투 계산용 개체를 만든다. 매치 상태를 변경하지 않는다.</summary>
internal static class CombatActorFactory
{
    public static void AddInventoryCombatActors(
        ICollection<ProximityCombatActor> actors,
        ProximityCombatActor spatialActor,
        PlayerOrbCollection inventory)
    {
        bool addedBoardOrb = false;

        int attackSlotIndex = 0;
        foreach (var item in inventory.GetAllItems()
                     .Where(item => item.Count > 0)
                     .OrderBy(item => item.ItemUid))
        {
            if (OrbData.IsRecoveryOrb(item.ItemId))
            {
                for (int stackIndex = 0; stackIndex < item.Count; stackIndex++)
                {
                    actors.Add(spatialActor with
                    {
                        WeaponItemId = item.ItemId,
                        WeaponItemUid = item.ItemUid,
                        WeaponStackIndex = stackIndex
                    });
                    addedBoardOrb = true;
                }
                continue;
            }

            if (!OrbData.TryGetColorAndTier(item.ItemId, out var orbColor, out _))
                continue;

            var combatData = BattleItemCombatData.Get(item.ItemId);
            if (combatData == null)
                continue;

            bool windActive = false;
            int sunStage = 0;
            bool waveArmed = false;
            bool orbEffectActive = false;

            for (int stackIndex = 0; stackIndex < item.Count; stackIndex++)
            {
                actors.Add(spatialActor with
                {
                    WeaponItemId = item.ItemId,
                    AttackRange = OrbData.GetAttackPattern(item.ItemId) ==
                                  OrbAttackPattern.AttackerArea
                        ? OrbData.GetWindPulseRadius(item.ItemId)
                        : combatData.AttackRange *
                          (windActive ? OrbData.WindAttackRangeMultiplier : 1f),
                    Damage = OrbData.GetBaseAttackDamage(combatData.Damage, orbColor),
                    AttackIntervalSeconds = combatData.AttackIntervalSeconds *
                                            OrbData.GetAttackIntervalMultiplier(item.ItemId) *
                                            OrbData.GetBaseAttackIntervalMultiplier(orbColor) *
                                            (windActive ? OrbData.WindAttackIntervalMultiplier : 1f),
                    ProjectileWidth = combatData.ProjectileWidth,
                    EffectDurationSeconds = combatData.EffectDurationSeconds,
                    MaxTargets = 1,
                    AdditionalTargetDamageMultiplier = 1f,
                    InitialBurstAttackCount = 0,
                    InitialBurstAttackIntervalMultiplier = 1f,
                    BurstRechargeSeconds = 0f,
                    // 슬롯마다 발사를 조금씩 어긋내 6칸이 같은 틱에 터지지 않게 한다.
                    // 0.15는 6번째 오브를 0.75초나 늦춰 조우 반응이 굼떠 보였다.
                    InitialAttackDelaySeconds = attackSlotIndex++ * 0.05f,
                    OrbEffectActive = orbEffectActive,
                    WeaponItemUid = item.ItemUid,
                    WeaponStackIndex = stackIndex,
                    SunResonanceStage = sunStage,
                    WaveResonanceArmed = waveArmed
                });
                addedBoardOrb = true;
            }
        }
        if (addedBoardOrb)
            return;

        actors.Add(spatialActor);
    }
    public static bool TryCreateSpatialActor(
        long playerId,
        MapId mapId,
        AreaType committedArea,
        Vector3f? position,
        out ProximityCombatActor actor)
    {
        actor = default;
        if (mapId == MapId.None || committedArea == AreaType.None || position == null)
            return false;

        var cell = ProximityCombatLineOfSight.WorldPositionToCell(mapId, position);
        var resolvedArea = GameMapData.GetCurrentArea(mapId, cell);
        if (resolvedArea == AreaType.None || resolvedArea != committedArea ||
            !GameMapData.IsMoveablePosition(mapId, cell))
        {
            return false;
        }

        actor = new ProximityCombatActor(
            playerId,
            resolvedArea,
            position,
            0,
            0f,
            0,
            0f,
            0f,
            0f,
            mapId,
            cell);
        return true;
    }


}
