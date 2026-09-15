using game_server.matches.monsters;
using network.common.data.models;
using game_server.players;
using Microsoft.Extensions.Logging;
using network.common;

namespace game_server.matches;

/// <summary>
///     매치 하나의 게임 로직을 50ms 주기로 순서대로 실행한다.
///     매치 잠금 안에서 입장 마감 확인, 아이템 획득, 몬스터 공급, 공통 이동, 전투,
///     환경 정산, 구역 폐쇄 확인 후 최종 상태를 동기화한다.
///
///     카운트다운 중에는 입장 확인, 몬스터 공급·등장 알림과 전투 준비만 수행한다.
///     환경 정산은 시작 후 5초마다, 구역 폐쇄 확인은 1초마다 수행하며,
///     처리가 늦어져도 밀린 횟수를 몰아서 실행하지 않는다.
///
///     처리 중 매치가 끝나거나 예외가 발생하면 해당 틱의 나머지 단계를 중단한다.
///     예외는 로그로 남기고 다음 틱을 계속한다.
///     Stop은 다음 틱을 중단하며, Completion을 기다리면 진행 중인 처리까지 끝난다.
/// </summary>
internal sealed class MatchTickLoop(
    MatchRuntime runtime,
    MatchRuntimeStore matchRuntimes,
    ILogger logger,
    PlayerPickupService playerPickups,
    MatchEntryFailureHandler entryFailureHandler,
    MatchCombatService combat,
    MatchFieldService field,
    MatchMoveService movement,
    MatchMonsterSpawnService monsterSpawns,
    MatchSynchronizationService synchronization,
    TimeProvider? timeProvider = null)
{
    private readonly PeriodicTimer _timer = new(TimeSpan.FromMilliseconds(50), timeProvider ?? TimeProvider.System);

    private long _lastAreaClosureSecond;
    private long _lastEnvironmentInterval;
    private int _stopping;

    public Task Completion { get; private set; } = Task.CompletedTask;
    public void Start() => Completion = Task.Run(RunAsync);
    public void Stop()
    {
        Volatile.Write(ref _stopping, 1);
        _timer.Dispose();
    }

    private async Task RunAsync()
    {
        try
        {
            while (await _timer.WaitForNextTickAsync())
            {
                if (Volatile.Read(ref _stopping) != 0 || runtime.IsEnded)
                {
                    break;
                }
                try
                {
                    ProcessTick();
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Match tick failed: MatchingId={MatchingId}", runtime.MatchingId);
                }
            }
        }
        finally
        {
            _timer.Dispose();
        }
    }

    internal void ProcessTick()
    {
        long matchingId = runtime.MatchingId;
        if (Volatile.Read(ref _stopping) != 0 || !ReferenceEquals(matchRuntimes.GetOrNull(matchingId), runtime) || runtime.IsEnded)
        {
            return;
        }

        using var scope = runtime.Enter();
        if (Volatile.Read(ref _stopping) != 0 || runtime.IsEnded || !ReferenceEquals(matchRuntimes.GetOrNull(matchingId), runtime))
        {
            return;
        }

        var utcNow = (timeProvider ?? TimeProvider.System).GetUtcNow().UtcDateTime;
        if (runtime.IsEntryTimedOut(utcNow))
        {
            entryFailureHandler.AbortMatchForEntryFailure(runtime);
            return;
        }

        bool isGameplayActive = runtime.IsGameplayActive(utcNow);
        if (isGameplayActive)
        {
            playerPickups.PickUp(runtime, runtime.GetAlivePlayers());
            if (runtime.IsEnded)
            {
                return;
            }
        }

        if (runtime.Mode != MatchMode.SoloMapValidation)
        {
            var participants = new List<PlayerPositionSnapshot>();
            foreach (var player in runtime.GetAlivePlayers())
            {
                if (player.Position != null)
                {
                    participants.Add(new PlayerPositionSnapshot(player.PlayerId, player.CurrentArea, player.Position));
                }
            }
            if (participants.Count > 0)
            {
                monsterSpawns.ProcessSupply(runtime, participants, utcNow, !isGameplayActive);
            }
        }

        synchronization.TrackNewObjects(runtime);
        movement.ProcessTick(runtime, utcNow);
        if (runtime.IsEnded)
        {
            return;
        }
        combat.ProcessTick(runtime);
        if (runtime.IsEnded)
        {
            return;
        }

        if (!isGameplayActive)
        {
            synchronization.ProcessTick(runtime, utcNow);
            return;
        }

        var startedAt = runtime.StartsAtUtc;
        if (startedAt.HasValue)
        {
            long elapsedSeconds = (utcNow - startedAt.Value).Ticks / TimeSpan.TicksPerSecond;
            long currentEnvironmentInterval = elapsedSeconds / Config.ENVIRONMENTAL_TICK_INTERVAL_SECONDS;
            if (currentEnvironmentInterval > _lastEnvironmentInterval)
            {
                _lastEnvironmentInterval = currentEnvironmentInterval;
                field.ProcessDamageTick(runtime);
                if (runtime.IsEnded)
                {
                    return;
                }
            }

            if (elapsedSeconds > _lastAreaClosureSecond)
            {
                _lastAreaClosureSecond = elapsedSeconds;
                field.ProcessClosureTick(runtime);
            }
        }
        synchronization.ProcessTick(runtime, utcNow);
    }
}
