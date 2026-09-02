using Microsoft.Extensions.Logging;
using network.interfaces;

namespace user_server.services;

/// <summary>
///     이탈 페널티(큐 지연) 계산과 Redis 기록. 이탈 1회당 30초, 상한 300초, 24시간마다 1회씩 감쇠한다.
///     상태는 Redis Hash 두 개(<c>leave_penalties</c>, <c>leave_penalty_decay_at</c>)뿐이며 프로세스 상태를 갖지 않는다.
///     읽기 실패는 0초 지연으로 fail-open하고 경고만 남긴다.
/// </summary>
internal sealed class LeavePenaltyService(ICacheHelper cacheHelper, ILogger logger)
{
    internal const string LeavePenaltyKey = "leave_penalties";
    internal const string LeavePenaltyDecayAtKey = "leave_penalty_decay_at";
    internal const int LeavePenaltySeconds = 30;
    internal const int MaxLeavePenaltySeconds = 300;
    internal const int PenaltyDecayIntervalHours = 24;
    internal const long PenaltyDecayIntervalSeconds = PenaltyDecayIntervalHours * 3600L;

    /// <summary>
    ///     큐 지연 초를 돌려준다: 이탈 1회당 30초, 상한 300초. 계산 전에 24시간 감쇠를 먼저 적용한다.
    /// </summary>
    public async Task<long> GetQueueDelayAsync(long playerId)
    {
        try
        {
            var value = await cacheHelper.HashGetAsync(LeavePenaltyKey, playerId);
            if (value.IsNullOrEmpty) return 0;

            long leaveCount = BitConverter.ToInt64((byte[])value!);
            if (leaveCount <= 0) return 0;

            leaveCount = await ApplyTimeDecayAsync(playerId, leaveCount);
            return ComputeQueueDelaySeconds(leaveCount);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Leave penalty lookup failed; queueing without delay: PlayerId={PlayerId}", playerId);
            return 0;
        }
    }

    /// <summary>
    ///     이탈 횟수 → 큐 지연 초. 순수 함수.
    /// </summary>
    internal static long ComputeQueueDelaySeconds(long leaveCount)
    {
        if (leaveCount <= 0) return 0;
        return Math.Min(leaveCount * LeavePenaltySeconds, MaxLeavePenaltySeconds);
    }

    /// <summary>
    ///     완료된 24시간 간격마다 이탈 1회를 제거한다. 감쇠 기준 시각은 남은 소수 간격을 보존하며 전진한다.
    ///     간격이 완료되지 않았으면(시계 역행 포함) 아무것도 바꾸지 않는다. 순수 함수.
    /// </summary>
    internal static LeavePenaltyDecay ComputeDecay(long leaveCount, long decayAtUnixSeconds, long nowUnixSeconds)
    {
        long elapsedSeconds = nowUnixSeconds - decayAtUnixSeconds;
        if (elapsedSeconds < PenaltyDecayIntervalSeconds)
            return new LeavePenaltyDecay(leaveCount, decayAtUnixSeconds, false);

        long decayCount = elapsedSeconds / PenaltyDecayIntervalSeconds;
        long remaining = Math.Max(0, leaveCount - decayCount);
        long newDecayAt = decayAtUnixSeconds + decayCount * PenaltyDecayIntervalSeconds;
        return new LeavePenaltyDecay(remaining, newDecayAt, true);
    }

    /// <summary>
    ///     감쇠 기준 시각이 없으면 지금으로 초기화하고, 있으면 <see cref="ComputeDecay" /> 결과를 Redis에 반영한다.
    /// </summary>
    private async Task<long> ApplyTimeDecayAsync(long playerId, long leaveCount)
    {
        try
        {
            var decayAtValue = await cacheHelper.HashGetAsync(LeavePenaltyDecayAtKey, playerId);
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            if (decayAtValue.IsNullOrEmpty)
            {
                await cacheHelper.HashSetAsync(LeavePenaltyDecayAtKey, playerId, BitConverter.GetBytes(now));
                return leaveCount;
            }

            long decayAt = BitConverter.ToInt64((byte[])decayAtValue!);
            LeavePenaltyDecay decay = ComputeDecay(leaveCount, decayAt, now);
            if (!decay.Decayed) return leaveCount;

            if (decay.RemainingCount <= 0)
            {
                await cacheHelper.HashDeleteAsync(LeavePenaltyKey, playerId);
                await cacheHelper.HashDeleteAsync(LeavePenaltyDecayAtKey, playerId);
                logger.LogInformation("Leave penalty fully decayed: PlayerId={PlayerId}", playerId);
                return decay.RemainingCount;
            }

            await cacheHelper.HashSetAsync(LeavePenaltyKey, playerId, BitConverter.GetBytes(decay.RemainingCount));
            await cacheHelper.HashSetAsync(LeavePenaltyDecayAtKey, playerId, BitConverter.GetBytes(decay.DecayAtUnixSeconds));
            logger.LogInformation("Leave penalty decayed: PlayerId={PlayerId}, RemainingCount={Count}",
                playerId, decay.RemainingCount);
            return decay.RemainingCount;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to decay leave penalty: PlayerId={PlayerId}", playerId);
            return leaveCount;
        }
    }

    /// <summary>
    ///     이탈 1회를 기록한다. 첫 이탈이면 감쇠 기준 시각도 지금으로 초기화한다.
    /// </summary>
    public async Task RecordLeaveAsync(long playerId)
    {
        try
        {
            var existing = await cacheHelper.HashGetAsync(LeavePenaltyKey, playerId);
            long count = existing.IsNullOrEmpty ? 1 : BitConverter.ToInt64((byte[])existing!) + 1;
            await cacheHelper.HashSetAsync(LeavePenaltyKey, playerId, BitConverter.GetBytes(count));

            if (existing.IsNullOrEmpty)
            {
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                await cacheHelper.HashSetAsync(LeavePenaltyDecayAtKey, playerId, BitConverter.GetBytes(now));
            }

            logger.LogInformation("Leave penalty recorded: PlayerId={PlayerId}, Count={Count}", playerId, count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Leave penalty record failed: PlayerId={PlayerId}", playerId);
        }
    }

    /// <summary>
    ///     정상 완주 시 이탈 1회를 되돌린다. 0이 되면 두 키를 모두 지운다.
    /// </summary>
    public async Task RecordCompletionAsync(long playerId)
    {
        try
        {
            var value = await cacheHelper.HashGetAsync(LeavePenaltyKey, playerId);
            if (value.IsNullOrEmpty) return;

            long leaveCount = BitConverter.ToInt64((byte[])value!);
            if (leaveCount <= 0) return;

            leaveCount = Math.Max(0, leaveCount - 1);

            if (leaveCount == 0)
            {
                await cacheHelper.HashDeleteAsync(LeavePenaltyKey, playerId);
                await cacheHelper.HashDeleteAsync(LeavePenaltyDecayAtKey, playerId);
            }
            else
            {
                await cacheHelper.HashSetAsync(LeavePenaltyKey, playerId, BitConverter.GetBytes(leaveCount));
            }

            logger.LogInformation("Leave penalty reduced after normal completion: PlayerId={PlayerId}, RemainingCount={Count}",
                playerId, leaveCount);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to reduce leave penalty after normal completion: PlayerId={PlayerId}", playerId);
        }
    }
}

/// <summary>
///     <see cref="LeavePenaltyService.ComputeDecay" /> 결과. <see cref="Decayed" />가 false면 Redis에 쓸 것이 없다.
/// </summary>
internal readonly record struct LeavePenaltyDecay(long RemainingCount, long DecayAtUnixSeconds, bool Decayed);
