using game_server.bots;
using game_server.items;
using game_server.combat;
using game_server.field;
using game_server.matches.entry;
using Microsoft.Extensions.Logging;
using network.common;

namespace game_server.matches;

/// <summary>
///     매치 하나의 게임 로직을 50ms 주기로 순서대로 실행한다.
///     매치 잠금 안에서 입장 확인·카운트다운, 아이템 획득, 전투,
///     환경 정산, 구역 폐쇄 확인, 봇 이동을 처리한다.
///
///     카운트다운 중에는 입장 확인과 전투 준비만 수행한다.
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

        var playerSessions = runtime.Sessions.Values.ToList();
        var activeSessions = playerSessions.Where(static session => session is { IsEliminated: false, IsGameEnded: false }).ToList();
        countdown.CheckEntryAndBroadcast([matchingId], playerSessions);
        if (runtime.IsEnded)
        {
            return;
        }

        var utcNow = (timeProvider ?? TimeProvider.System).GetUtcNow().UtcDateTime;
        bool isGameplayActive = MatchStartGate.IsGameplayActive(matchingId, utcNow);
        if (isGameplayActive)
        {
            groundItemAutoPickup.Process(runtime, activeSessions);
            if (runtime.IsEnded)
            {
                return;
            }
        }

        combat.ProcessTick(matchingId, activeSessions);
        if (runtime.IsEnded || !isGameplayActive)
        {
            return;
        }

        var startedAt = MatchStartGate.GetGameplayStartedAtUtc(matchingId);
        if (startedAt.HasValue)
        {
            long elapsedSeconds = (utcNow - startedAt.Value).Ticks / TimeSpan.TicksPerSecond;
            long currentEnvironmentInterval = elapsedSeconds / Config.ENVIRONMENTAL_TICK_INTERVAL_SECONDS;
            if (currentEnvironmentInterval > _lastEnvironmentInterval)
            {
                _lastEnvironmentInterval = currentEnvironmentInterval;
                environment.ProcessTick(runtime, activeSessions);
                if (runtime.IsEnded)
                {
                    return;
                }
            }

            if (elapsedSeconds > _lastAreaClosureSecond)
            {
                _lastAreaClosureSecond = elapsedSeconds;
                zones.ProcessTick(matchingId, playerSessions.ToArray());
            }
        }

        if (runtime.IsEnded || !runtime.Bots.HasBots(matchingId))
        {
            return;
        }

        botMovement.ProcessTick(runtime, botDecisions.DecideMovement);
    }
}
