using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Threading.Channels;
using network.common.data;
using network.common.data.models;

namespace game_server.services;

public sealed class RoomEventWorldManager : IAsyncDisposable
{
    private readonly ConcurrentDictionary<long, MatchingRuntime> _matchingRuntimes = new();
    private readonly ConcurrentDictionary<long, byte> _closedMatchingIds = new();
    private readonly InGameInventoryManager _inventoryManager;
    private readonly IRoomEventActorGateway _actorGateway;
    private readonly TimeProvider _timeProvider;
    private int _disposed;

    public RoomEventWorldManager(
        InGameInventoryManager inventoryManager,
        IRoomEventActorGateway actorGateway,
        TimeProvider? timeProvider = null)
    {
        _inventoryManager = inventoryManager ?? throw new ArgumentNullException(nameof(inventoryManager));
        _actorGateway = actorGateway ?? throw new ArgumentNullException(nameof(actorGateway));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task<RoomEventActivationResult> EnqueueActivateAsync(RoomEventActivationCommand command)
    {
        ThrowIfDisposed();
        if (_closedMatchingIds.ContainsKey(command.MatchingId))
            return Task.FromResult(new RoomEventActivationResult(false, RoomEventCommandError.MatchingClosed, null));

        if (command.MatchingId <= 0 ||
            command.EventId <= 0 ||
            command.AreaType == global::network.common.AreaType.None ||
            command.InteractId <= 0 ||
            string.IsNullOrWhiteSpace(command.RequiredResponseTag))
        {
            return Task.FromResult(new RoomEventActivationResult(false, RoomEventCommandError.InvalidState, null));
        }

        var runtime = _matchingRuntimes.GetOrAdd(
            command.MatchingId,
            matchingId => new MatchingRuntime(
                matchingId,
                _inventoryManager,
                _actorGateway,
                _timeProvider));
        return runtime.EnqueueActivateAsync(command);
    }

    public Task<RoomEventCommandResult> EnqueueInterventionAsync(RoomEventInterventionCommand command)
    {
        ThrowIfDisposed();
        if (_closedMatchingIds.ContainsKey(command.MatchingId))
            return Task.FromResult(Failed(RoomEventCommandError.MatchingClosed));

        return _matchingRuntimes.TryGetValue(command.MatchingId, out var runtime)
            ? runtime.EnqueueInterventionAsync(command)
            : Task.FromResult(Failed(RoomEventCommandError.MatchingNotFound));
    }

    public Task<RoomEventCommandResult> EnqueueExpireAsync(long matchingId, long eventInstanceId)
    {
        ThrowIfDisposed();
        if (_closedMatchingIds.ContainsKey(matchingId))
            return Task.FromResult(Failed(RoomEventCommandError.MatchingClosed));

        return _matchingRuntimes.TryGetValue(matchingId, out var runtime)
            ? runtime.EnqueueExpireAsync(eventInstanceId)
            : Task.FromResult(Failed(RoomEventCommandError.MatchingNotFound));
    }

    public async ValueTask RemoveMatchingStateAsync(long matchingId)
    {
        _closedMatchingIds.TryAdd(matchingId, 0);
        if (_matchingRuntimes.TryRemove(matchingId, out var runtime))
            await runtime.StopAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        var runtimes = _matchingRuntimes.ToArray();
        _matchingRuntimes.Clear();
        foreach (var runtime in runtimes)
            await runtime.Value.StopAsync();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }

    private static RoomEventCommandResult Failed(
        RoomEventCommandError error,
        RoomEventWorldState? state = null)
    {
        return new RoomEventCommandResult(false, error, state, null, 0, 0);
    }

    private sealed class MatchingRuntime
    {
        private const int MaximumContributionPerItemId = 5;

        private readonly Channel<QueuedCommand> _commands = Channel.CreateUnbounded<QueuedCommand>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });
        private readonly Dictionary<long, RoomEventWorldState> _instances = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly InGameInventoryManager _inventoryManager;
        private readonly IRoomEventActorGateway _actorGateway;
        private readonly TimeProvider _timeProvider;
        private readonly long _matchingId;
        private readonly Task _worker;
        private long _nextEventInstanceId;

        public MatchingRuntime(
            long matchingId,
            InGameInventoryManager inventoryManager,
            IRoomEventActorGateway actorGateway,
            TimeProvider timeProvider)
        {
            _matchingId = matchingId;
            _inventoryManager = inventoryManager;
            _actorGateway = actorGateway;
            _timeProvider = timeProvider;
            _worker = ProcessCommandsAsync();
        }

        public Task<RoomEventActivationResult> EnqueueActivateAsync(RoomEventActivationCommand command)
        {
            var completion = NewCompletion<RoomEventActivationResult>();
            return WriteOrFail(new ActivateQueuedCommand(command, completion), completion);
        }

        public Task<RoomEventCommandResult> EnqueueInterventionAsync(RoomEventInterventionCommand command)
        {
            var completion = NewCompletion<RoomEventCommandResult>();
            return WriteOrFail(new InterventionQueuedCommand(command, completion), completion);
        }

        public Task<RoomEventCommandResult> EnqueueExpireAsync(long eventInstanceId)
        {
            var completion = NewCompletion<RoomEventCommandResult>();
            return WriteOrFail(new ExpireQueuedCommand(eventInstanceId, completion), completion);
        }

        public async Task StopAsync()
        {
            _stop.Cancel();
            _commands.Writer.TryComplete();
            await _worker;
            _stop.Dispose();
        }

        private async Task ProcessCommandsAsync()
        {
            await foreach (var command in _commands.Reader.ReadAllAsync())
            {
                try
                {
                    switch (command)
                    {
                        case ActivateQueuedCommand activate:
                            HandleActivate(activate);
                            break;
                        case InterventionQueuedCommand intervention:
                            HandleIntervention(intervention);
                            break;
                        case ExpireQueuedCommand expire:
                            HandleExpire(expire);
                            break;
                    }
                }
                catch (Exception exception)
                {
                    command.SetException(exception);
                }
            }
        }

        private void HandleActivate(ActivateQueuedCommand queued)
        {
            var command = queued.Command;
            var roomEvent = GameRoomEventData.Get(command.EventId);
            if (roomEvent == null ||
                !roomEvent.UsesWorldState ||
                roomEvent.DurationSeconds <= 0 ||
                roomEvent.TargetContribution <= 0 ||
                !roomEvent.ResponseTagPool.Contains(command.RequiredResponseTag, StringComparer.OrdinalIgnoreCase))
            {
                queued.Completion.SetResult(new RoomEventActivationResult(
                    false,
                    RoomEventCommandError.InvalidResponseTag,
                    null));
                return;
            }

            var existing = _instances.Values.FirstOrDefault(state =>
                state.EventId == command.EventId && state.AreaType == command.AreaType);
            if (existing != null)
            {
                queued.Completion.SetResult(new RoomEventActivationResult(
                    false,
                    RoomEventCommandError.AlreadyActive,
                    existing));
                return;
            }

            DateTime nowUtc = GetUtcNow();
            long eventInstanceId = ++_nextEventInstanceId;
            var state = new RoomEventWorldState(
                eventInstanceId,
                command.EventId,
                command.AreaType,
                command.InteractId,
                RoomEventWorldStatus.Active,
                StateVersion: 1,
                ActivatedAtUtc: nowUtc,
                ExpiresAtUtc: nowUtc.AddSeconds(roomEvent.DurationSeconds),
                command.RequiredResponseTag,
                CurrentContribution: 0,
                roomEvent.TargetContribution,
                ImmutableDictionary<long, int>.Empty,
                ImmutableDictionary<int, int>.Empty,
                ImmutableHashSet<int>.Empty,
                TimeExtensionsUsed: 0,
                roomEvent.MaxTimeExtensions,
                roomEvent.TimeExtensionSeconds,
                ManualResponsesUsed: 0,
                roomEvent.MaxManualResponses,
                roomEvent.MaxContributionPerPlayer);
            _instances[eventInstanceId] = state;
            ScheduleExpiration(eventInstanceId, state.ExpiresAtUtc);

            queued.Completion.SetResult(new RoomEventActivationResult(
                true,
                RoomEventCommandError.None,
                state));
        }

        private void HandleIntervention(InterventionQueuedCommand queued)
        {
            var command = queued.Command;
            if (!_instances.TryGetValue(command.EventInstanceId, out var state))
            {
                queued.Completion.SetResult(Failed(RoomEventCommandError.InstanceNotFound));
                return;
            }

            DateTime nowUtc = GetUtcNow();
            if (nowUtc >= state.ExpiresAtUtc && state.IsActive)
            {
                var overrun = FinalizeOverrun(state);
                queued.Completion.SetResult(Failed(RoomEventCommandError.Expired, overrun));
                return;
            }

            if (state.StateVersion != command.ExpectedStateVersion)
            {
                queued.Completion.SetResult(Failed(RoomEventCommandError.StaleVersion, state));
                return;
            }

            if (!state.IsActive)
            {
                queued.Completion.SetResult(Failed(RoomEventCommandError.Inactive, state));
                return;
            }

            if (!_actorGateway.IsPlayerInArea(_matchingId, command.ActorPlayerId, state.AreaType))
            {
                queued.Completion.SetResult(Failed(RoomEventCommandError.InvalidLocation, state));
                return;
            }

            if (!GameRoomEventData.TryGetChoice(state.EventId, command.ChoiceId, out _, out var choice))
            {
                queued.Completion.SetResult(Failed(RoomEventCommandError.InvalidChoice, state));
                return;
            }

            RoomEventCommandResult result = choice.WorldEffectId switch
            {
                "contribute_material" => ApplyItemContribution(command, state, choice, expectedPower: 1),
                "contribute_crafted_item" => ApplyItemContribution(command, state, choice, expectedPower: null),
                "extend_time" => ApplyTimeExtension(command, state, choice),
                "manual_response" => ApplyManualResponse(command, state, choice),
                _ => Failed(RoomEventCommandError.InvalidChoice, state)
            };
            queued.Completion.SetResult(result);
        }

        private RoomEventCommandResult ApplyItemContribution(
            RoomEventInterventionCommand command,
            RoomEventWorldState state,
            RoomEventChoiceInfoData choice,
            int? expectedPower)
        {
            if (choice.ConsumeItemCount != 1 || command.SelectedItemId <= 0)
                return Failed(RoomEventCommandError.InvalidChoice, state);

            var responseItem = GameRoomEventResponseItemData.Get(command.SelectedItemId);
            if (responseItem == null || !responseItem.ResponseTags.Contains(state.RequiredResponseTag))
                return Failed(RoomEventCommandError.InvalidItem, state);

            int power = responseItem.ResponsePower;
            if ((expectedPower.HasValue && power != expectedPower.Value) ||
                (!expectedPower.HasValue && power is not (3 or 5)))
            {
                return Failed(RoomEventCommandError.InvalidItemStage, state);
            }

            int playerContribution = state.ContributionByPlayer.GetValueOrDefault(command.ActorPlayerId);
            if (playerContribution + power > state.MaxContributionPerPlayer)
                return Failed(RoomEventCommandError.ContributionLimit, state);

            int itemContribution = state.ContributionByItemId.GetValueOrDefault(command.SelectedItemId);
            if (itemContribution + power > MaximumContributionPerItemId)
                return Failed(RoomEventCommandError.ItemContributionLimit, state);

            if (!_inventoryManager.TryRemoveOneByItemId(
                    _matchingId,
                    command.ActorPlayerId,
                    command.SelectedItemId,
                    out var consumedItem))
            {
                return Failed(RoomEventCommandError.InsufficientItem, state);
            }

            var next = state with
            {
                StateVersion = state.StateVersion + 1,
                CurrentContribution = state.CurrentContribution + power,
                ContributionByPlayer = state.ContributionByPlayer.SetItem(
                    command.ActorPlayerId,
                    playerContribution + power),
                ContributionByItemId = state.ContributionByItemId.SetItem(
                    command.SelectedItemId,
                    itemContribution + power),
                DistinctContributedItemIds = state.DistinctContributedItemIds.Add(command.SelectedItemId)
            };
            next = FinalizeContainedIfReady(next);
            _instances[state.EventInstanceId] = next;

            return new RoomEventCommandResult(
                true,
                RoomEventCommandError.None,
                next,
                Snapshot(consumedItem),
                AppliedStaminaDelta: 0,
                AppliedMentalDelta: 0);
        }

        private RoomEventCommandResult ApplyTimeExtension(
            RoomEventInterventionCommand command,
            RoomEventWorldState state,
            RoomEventChoiceInfoData choice)
        {
            if (command.SelectedItemId != 0 || choice.ConsumeItemCount != 0)
                return Failed(RoomEventCommandError.InvalidChoice, state);
            if (state.TimeExtensionsUsed >= state.MaxTimeExtensions)
                return Failed(RoomEventCommandError.ExtensionLimit, state);
            if (!_actorGateway.TryApplyResourceDelta(
                    _matchingId,
                    command.ActorPlayerId,
                    choice.StaminaDelta,
                    choice.MentalDelta))
            {
                return Failed(RoomEventCommandError.ResourceCostFailed, state);
            }

            var next = state with
            {
                StateVersion = state.StateVersion + 1,
                ExpiresAtUtc = state.ExpiresAtUtc.AddSeconds(state.TimeExtensionSeconds),
                TimeExtensionsUsed = state.TimeExtensionsUsed + 1
            };
            _instances[state.EventInstanceId] = next;
            ScheduleExpiration(state.EventInstanceId, next.ExpiresAtUtc);

            return new RoomEventCommandResult(
                true,
                RoomEventCommandError.None,
                next,
                null,
                choice.StaminaDelta,
                choice.MentalDelta);
        }

        private RoomEventCommandResult ApplyManualResponse(
            RoomEventInterventionCommand command,
            RoomEventWorldState state,
            RoomEventChoiceInfoData choice)
        {
            if (command.SelectedItemId != 0 || choice.ConsumeItemCount != 0)
                return Failed(RoomEventCommandError.InvalidChoice, state);
            if (state.ManualResponsesUsed >= state.MaxManualResponses)
                return Failed(RoomEventCommandError.ManualResponseLimit, state);

            int playerContribution = state.ContributionByPlayer.GetValueOrDefault(command.ActorPlayerId);
            if (playerContribution + 1 > state.MaxContributionPerPlayer)
                return Failed(RoomEventCommandError.ContributionLimit, state);
            if (!_actorGateway.TryApplyResourceDelta(
                    _matchingId,
                    command.ActorPlayerId,
                    choice.StaminaDelta,
                    choice.MentalDelta))
            {
                return Failed(RoomEventCommandError.ResourceCostFailed, state);
            }

            var next = state with
            {
                StateVersion = state.StateVersion + 1,
                CurrentContribution = state.CurrentContribution + 1,
                ContributionByPlayer = state.ContributionByPlayer.SetItem(
                    command.ActorPlayerId,
                    playerContribution + 1),
                ManualResponsesUsed = state.ManualResponsesUsed + 1
            };
            next = FinalizeContainedIfReady(next);
            _instances[state.EventInstanceId] = next;

            return new RoomEventCommandResult(
                true,
                RoomEventCommandError.None,
                next,
                null,
                choice.StaminaDelta,
                choice.MentalDelta);
        }

        private void HandleExpire(ExpireQueuedCommand queued)
        {
            if (!_instances.TryGetValue(queued.EventInstanceId, out var state))
            {
                queued.Completion.TrySetResult(Failed(RoomEventCommandError.InstanceNotFound));
                return;
            }

            if (!state.IsActive)
            {
                queued.Completion.TrySetResult(Failed(RoomEventCommandError.Inactive, state));
                return;
            }

            if (GetUtcNow() < state.ExpiresAtUtc)
            {
                queued.Completion.TrySetResult(Failed(RoomEventCommandError.InvalidState, state));
                return;
            }

            var overrun = FinalizeOverrun(state);
            queued.Completion.TrySetResult(new RoomEventCommandResult(
                true,
                RoomEventCommandError.None,
                overrun,
                null,
                0,
                0));
        }

        private RoomEventWorldState FinalizeOverrun(RoomEventWorldState state)
        {
            var overrun = state with
            {
                Status = RoomEventWorldStatus.Overrun,
                StateVersion = state.StateVersion + 1
            };
            _instances[state.EventInstanceId] = overrun;
            return overrun;
        }

        private static RoomEventWorldState FinalizeContainedIfReady(RoomEventWorldState state)
        {
            return state.CurrentContribution >= state.TargetContribution &&
                   state.DistinctContributedItemIds.Count >= 2
                ? state with { Status = RoomEventWorldStatus.Contained }
                : state;
        }

        private void ScheduleExpiration(long eventInstanceId, DateTime expiresAtUtc)
        {
            TimeSpan delay = expiresAtUtc - GetUtcNow();
            if (delay < TimeSpan.Zero)
                delay = TimeSpan.Zero;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(delay, _stop.Token);
                    await EnqueueExpireAsync(eventInstanceId);
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested)
                {
                }
                catch (ObjectDisposedException)
                {
                }
            });
        }

        private DateTime GetUtcNow() => _timeProvider.GetUtcNow().UtcDateTime;

        private static InGameItemInfo? Snapshot(InGameItemInfo? item)
        {
            return item == null
                ? null
                : new InGameItemInfo
                {
                    ItemUid = item.ItemUid,
                    ItemId = item.ItemId,
                    Count = item.Count,
                    GiftState = item.GiftState
                };
        }

        private static RoomEventCommandResult Failed(
            RoomEventCommandError error,
            RoomEventWorldState? state = null)
        {
            return new RoomEventCommandResult(false, error, state, null, 0, 0);
        }

        private static TaskCompletionSource<T> NewCompletion<T>() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private Task<T> WriteOrFail<T>(QueuedCommand command, TaskCompletionSource<T> completion)
        {
            if (!_commands.Writer.TryWrite(command))
                completion.TrySetException(new ObjectDisposedException(nameof(RoomEventWorldManager)));

            return completion.Task;
        }

        private abstract record QueuedCommand
        {
            public abstract void SetException(Exception exception);
        }

        private sealed record ActivateQueuedCommand(
            RoomEventActivationCommand Command,
            TaskCompletionSource<RoomEventActivationResult> Completion) : QueuedCommand
        {
            public override void SetException(Exception exception) => Completion.TrySetException(exception);
        }

        private sealed record InterventionQueuedCommand(
            RoomEventInterventionCommand Command,
            TaskCompletionSource<RoomEventCommandResult> Completion) : QueuedCommand
        {
            public override void SetException(Exception exception) => Completion.TrySetException(exception);
        }

        private sealed record ExpireQueuedCommand(
            long EventInstanceId,
            TaskCompletionSource<RoomEventCommandResult> Completion) : QueuedCommand
        {
            public override void SetException(Exception exception) => Completion.TrySetException(exception);
        }
    }
}
