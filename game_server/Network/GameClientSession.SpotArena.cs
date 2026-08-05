using game_server.services;
using network.common;
using network.common.data.models;

namespace game_server.network;

public partial class GameClientSession
{
    internal void SetSpotArenaTarget(long targetPlayerId)
    {
        TargetPlayerId = targetPlayerId;
    }

    internal void PlaceAtSpotArenaStart(AreaType area, Cell cell)
    {
        if (!PlayerId.HasValue || IsEliminated)
            return;

        AreaType oldArea = CurrentArea;
        CurrentState = PlayerState.Idle;
        CurrentArea = area;
        _lastValidCell = Cell.Clone(cell);
        _lastValidatedPosition = CellToWorldPosition(cell);
        _lastValidatedRotation = 0f;
        SendMovementCorrection(0);
        if (oldArea != area)
            _ = HandleAreaChange(oldArea, area);
    }

    internal bool ApplySpotArenaCombatHit(
        long sourcePlayerId,
        AreaType area,
        int weaponItemId,
        int damage)
    {
        if (!PlayerId.HasValue || IsEliminated || _spotArenaRespawning || damage <= 0)
            return false;

        int before = Corruption;
        ModifyStats(
            corruptionDelta: damage,
            attackerPlayerId: sourcePlayerId,
            deferElimination: true);
        SendEncounterEvent(
            sourcePlayerId,
            area,
            ProximityAutoAttackTakenEventType,
            0,
            weaponItemId,
            damage);

        bool lethal = before < MaxCorruption && Corruption >= MaxCorruption;
        if (lethal)
            _spotArenaRespawning = true;
        return lethal;
    }

    internal bool ApplySpotArenaMonsterHit(int monsterId, int damage)
    {
        if (!PlayerId.HasValue || IsEliminated || _spotArenaRespawning || damage <= 0)
            return false;

        int before = Corruption;
        ModifyStats(corruptionDelta: damage, deferElimination: true);
        SendEncounterEvent(
            PlayerId.Value,
            CurrentArea,
            EmotionAfterimageMonsterAttackTakenEventType,
            0,
            monsterId,
            damage);

        bool lethal = before < MaxCorruption && Corruption >= MaxCorruption;
        if (lethal)
            _spotArenaRespawning = true;
        return lethal;
    }

    internal void CompleteSpotArenaRespawn(AreaType area, Cell cell)
    {
        if (!PlayerId.HasValue || IsEliminated)
            return;

        AreaType oldArea = CurrentArea;
        _spotArenaRespawning = false;
        Corruption = InitialCorruption;
        Stamina = InitialStamina;
        CurrentState = PlayerState.Idle;
        CurrentArea = area;
        _lastValidCell = Cell.Clone(cell);
        _lastValidatedPosition = CellToWorldPosition(cell);
        _lastValidatedRotation = 0f;
        SendPlayerStatsUpdate(0, 0);
        SendMovementCorrection(0);
        if (oldArea != area)
            _ = HandleAreaChange(oldArea, area);
    }

    internal void EnterSpotArenaSpectatorMode()
    {
        _spotArenaRespawning = false;
        ManittoStatus = ManittoStatus.SPECTATING;
    }

    internal void SendSpotArenaGameResult(
        List<GameClientSession> allSessions,
        long winnerPlayerId,
        string endReason)
    {
        SendGameResult(
            allSessions,
            winnerPlayerId,
            isTimeout: endReason == "spot_timeout",
            CurrentMapSubId,
            endReason,
            winnerPlayerId == 0 ? "draw" : "spot_hp_then_damage");
    }
}
