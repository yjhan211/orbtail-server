using network.common;
using network.common.data.models;

namespace game_server.matches.combat;

public readonly record struct PlayerPositionSnapshot(
    long PlayerId,
    AreaType Area,
    Vector3f Position);

public readonly record struct MonsterContactDamage(
    int MonsterId,
    long TargetPlayerId,
    AreaType Area,
    int Damage);

public readonly record struct PendingMonsterHit(
    long CombatTargetId,
    long AttackerId,
    int Damage,
    DateTime ApplyAtUtc);

public readonly record struct PendingWaveAttack(
    long OwnerId,
    AreaType Area,
    Vector3f Position,
    int Damage,
    float Radius,
    int SourceItemId,
    DateTime ExplodeAtUtc);

public readonly record struct ProximityCombatActor(
    long PlayerId,
    AreaType Area,
    Vector3f Position,
    int WeaponItemId,
    float AttackRange,
    int Damage,
    float AttackIntervalSeconds,
    float ProjectileWidth = 0f,
    float EffectDurationSeconds = 0f,
    MapId MapId = MapId.None,
    Cell? Cell = null,
    int MaxTargets = 1,
    float AdditionalTargetDamageMultiplier = 1f,
    int InitialBurstAttackCount = 0,
    float InitialBurstAttackIntervalMultiplier = 1f,
    float BurstRechargeSeconds = 0f,
    long WeaponItemUid = 0,
    int WeaponStackIndex = 0,
    int SunResonanceStage = 0,
    bool WaveResonanceArmed = false,
    float InitialAttackDelaySeconds = 0f,
    bool IsMonsterTarget = false,
    bool IsCoreMonsterTarget = false,
    int TargetPriority = -1,
    bool Untargetable = false,
    int TrailOrdinal = 0);

public readonly record struct ProximityCombatAttack(
    long AttackerPlayerId,
    long TargetPlayerId,
    AreaType Area,
    int WeaponItemId,
    int Damage,
    float ProjectileWidth,
    float EffectDurationSeconds,
    int CandidateTargetCount = 0,
    int SunResonanceStage = 0,
    bool WaveResonanceArmed = false,
    bool IsResonanceProc = false,
    bool IsWaveAreaAttack = false,
    bool IsWaveAreaSecondary = false,
    bool IsWindAreaAttack = false,
    bool IsWindAreaSecondary = false,
    long AttackerItemUid = 0,
    Vector3f? Origin = null,
    Vector3f? AnchorPosition = null,
    int AttackerTrailOrdinal = 0);

public sealed class CutRetaliationWindow
{
    public DateTime OpenedAtUtc;
    public DateTime ExpiresAtUtc;
    public int BlockedDamage;
    public int BlockedHits;
    public int BlockedCuts;
    public bool Retaliated;
    public AreaType OpenedArea;
}
