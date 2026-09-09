using game_server.matches.combat;
using game_server.matches.field;
using game_server.matches.entry;
using game_server.services;
using Microsoft.Extensions.Logging;

namespace game_server.matches;

/// <summary>
///     매치 하나의 50ms 순차 루프. 이전 처리가 끝나야 다음 틱을 받으며 밀린 틱은 합쳐진다.
///     매치 잠금을 기다린 뒤 카운트다운·전투·환경 정산·구역 폐쇄·봇 이동을 실행한다.
///     한 단계가 실패하면 그 틱의 나머지 처리를 중단하고 로그를 남긴 뒤 다음 틱을 계속한다.
///     Stop은 다음 틱을 막고, Completion은 실행 중인 처리까지 끝났음을 나타낸다.
/// </summary>
internal sealed class MatchTickLoop(
    MatchRuntime runtime,
    MatchRuntimeStore matchRuntimes,
    ILogger logger,
    GroundItemAutoPickupService groundItemAutoPickup,
    MatchCountdownService countdown,
    MatchCombatService combat,
    MatchEnvironmentService environment,
    BotMovementService botMovement,
    BotDecisionService botDecisions,
    MatchZoneService zones,
    TimeProvider? timeProvider = null)
{
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
        if (Volatile.Read(ref _stopping) != 0 || !ReferenceEquals(matchRuntimes.GetOrNull(matchingId), runtime) || runtime.IsEnded)
        {
            return;
        }

        using var scope = runtime.Enter();
        if (Volatile.Read(ref _stopping) != 0 || runtime.IsEnded || !ReferenceEquals(matchRuntimes.GetOrNull(matchingId), runtime))
        {
            return;
        }

        var playerSessions = runtime.Sessions.Values.ToList().Where(static session => session.PlayerId.HasValue).ToList();
        var activeSessions = playerSessions.Where(static session => session is { IsEliminated: false, IsGameEnded: false }).ToList();

        countdown.CheckEntryAndBroadcast([matchingId], playerSessions);
        if (runtime.IsEnded)
        {
            return;
        }

        if (MatchStartGate.IsGameplayActive(matchingId))
        {
            groundItemAutoPickup.Process(runtime, activeSessions);
            if (runtime.IsEnded)
            {
                return;
            }
        }

        combat.ProcessTick(matchingId, activeSessions);
        if (runtime.IsEnded)
        {
            return;
        }

        if (runtime.TickSchedule.TryBeginEnvironmentalTick(DateTime.UtcNow, MatchStartGate.GetGameplayStartedAtUtc(matchingId), runtime.IsEnded))
        {
            environment.ProcessTick(runtime, activeSessions);
            if (runtime.IsEnded)
            {
                return;
            }
        }

        if (runtime.TickSchedule.TryBeginAreaClosureTick(DateTime.UtcNow, MatchStartGate.GetGameplayStartedAtUtc(matchingId), runtime.IsEnded))
        {
            zones.ProcessTick(matchingId, playerSessions.ToArray());
        }

        if (runtime.IsEnded || !MatchStartGate.IsGameplayActive(matchingId) || !runtime.Bots.HasBots(matchingId))
        {
            return;
        }

        botMovement.ProcessTick(runtime, botDecisions.DecideMovement);
    }
}
