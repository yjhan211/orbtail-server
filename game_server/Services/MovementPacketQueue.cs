namespace game_server.services;

/// <summary>
///     세션의 요청 순서를 지키면서 아직 처리하지 않은 연속 이동만 최신 요청으로 합친다.
///     이동 사이에는 최소 간격을 두되 마지막 이동도 반드시 처리한다. 다른 요청은 합치지 않으며,
///     이동 → 탐색 → 이동처럼 행동 요청을 사이에 둔 이동끼리도 합치지 않는다.
///     각 요청의 Task는 실제 처리(또는 연결 종료로 취소)가 끝나야 완료된다.
/// </summary>
internal sealed class MovementPacketQueue(Func<bool> canDispatch, TimeProvider? timeProvider = null)
{
    private readonly object _gate = new();
    private readonly LinkedList<PendingPacket> _pending = new();
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private bool _draining;
    private long? _lastMovementCompletedAt;

    public Task EnqueueAsync(Func<Task> dispatch, uint? movementSequence = null)
    {
        Task completion;
        bool startDrain;
        lock (_gate)
        {
            if (movementSequence is { } incoming && _pending.Last?.Value is { Sequence: { } previous } tail)
            {
                // 같은 순번의 정지 패킷도 최신 값이다. uint 순번의 wrap-around도 허용한다.
                if (unchecked(incoming - previous) <= uint.MaxValue / 2)
                {
                    tail.Sequence = incoming;
                    tail.Dispatch = dispatch;
                }
                return tail.Completion.Task;
            }

            var pending = new PendingPacket(dispatch, movementSequence);
            _pending.AddLast(pending);
            completion = pending.Completion.Task;
            startDrain = !_draining;
            _draining = true;
        }

        if (startDrain) _ = DrainAsync();
        return completion;
    }

    private async Task DrainAsync()
    {
        while (true)
        {
            PendingPacket pending;
            lock (_gate)
            {
                if (_pending.First == null)
                {
                    _draining = false;
                    return;
                }
                pending = _pending.First.Value;
            }

            Exception? error = null;
            try
            {
                // 기다리는 동안에도 pending을 큐에 두어 더 최신 이동으로 교체할 수 있게 한다.
                if (pending.IsMovement && _lastMovementCompletedAt is { } last)
                {
                    while (canDispatch())
                    {
                        TimeSpan remaining = MovementValidationPolicy.MinimumMovementInterval
                            - _time.GetElapsedTime(last);
                        if (remaining <= TimeSpan.Zero) break;
                        await Task.Delay(remaining, _time);
                    }
                }
            }
            catch (Exception ex)
            {
                error = ex;
            }

            Func<Task> dispatch;
            lock (_gate)
            {
                _pending.RemoveFirst();
                dispatch = pending.Dispatch;
            }

            try
            {
                if (error == null && canDispatch()) await dispatch();
            }
            catch (Exception ex)
            {
                error = ex;
            }
            finally
            {
                if (pending.IsMovement) _lastMovementCompletedAt = _time.GetTimestamp();
                if (error == null) pending.Completion.TrySetResult();
                else pending.Completion.TrySetException(error);
            }
        }
    }

    private sealed class PendingPacket(Func<Task> dispatch, uint? sequence)
    {
        public Func<Task> Dispatch { get; set; } = dispatch;
        public uint? Sequence { get; set; } = sequence;
        public bool IsMovement { get; } = sequence.HasValue;
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
