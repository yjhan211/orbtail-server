using game_server.matches.monsters;
using game_server.players;
using Microsoft.Extensions.Logging;

namespace game_server.matches;

/// <summary>
///     매치 하나의 게임 로직을 50ms 주기로 순서대로 실행한다.
///     매치 잠금 안에서 입장 마감 확인, 아이템 획득, 몬스터 공급, 공통 이동, 전투, 필드 틱을 돌린 뒤 최종 상태를 동기화한다.
///     루프는 순서만 정하며, 시작 전에 무엇을 건너뛸지, 몇 초마다 돌지는 각 서비스가 정한다.
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
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly PeriodicTimer _timer = new(TimeSpan.FromMilliseconds(50), timeProvider ?? TimeProvider.System);
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
        if (Volatile.Read(ref _stopping) != 0 || runtime.IsEnded || !ReferenceEquals(matchRuntimes.GetOrNull(matchingId), runtime))
        {
            return;
        }

        using var scope = runtime.Enter();
        if (Volatile.Read(ref _stopping) != 0 || runtime.IsEnded || !ReferenceEquals(matchRuntimes.GetOrNull(matchingId), runtime))
        {
            return;
        }

        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        if (runtime.IsEntryTimedOut(utcNow))
        {
            entryFailureHandler.AbortMatchForEntryFailure(runtime);
            return;
        }

        playerPickups.ProcessTick(runtime, utcNow);
        if (runtime.IsEnded)
        {
            return;
        }

        monsterSpawns.ProcessTick(runtime, utcNow);
        synchronization.InitializeComparisonSnapshots(runtime);
        movement.ProcessTick(runtime, utcNow);
        if (runtime.IsEnded)
        {
            return;
        }

        combat.ProcessTick(runtime, utcNow);
        if (runtime.IsEnded)
        {
            return;
        }

        field.ProcessTick(runtime, utcNow);
        if (runtime.IsEnded)
        {
            return;
        }

        synchronization.ProcessTick(runtime, utcNow);
    }
}
