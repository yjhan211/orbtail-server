using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using network.interfaces;

namespace game_server.controllers;

public abstract class BaseMapManager(
    ILogger logger,
    INatsClient natsClient,
    ICacheHelper cacheHelper,
    IServerConfig serverConfig)
{
    private readonly object _handlerGate = new();
    private readonly ConcurrentDictionary<long, Task> _handlerTasks = new();
    private long _nextHandlerId;
    private bool _stopping;

    protected readonly ICacheHelper CacheHelper = cacheHelper;
    protected readonly ILogger Logger = logger;
    protected readonly SemaphoreSlim MapLock = new(1, 1);
    protected readonly INatsClient NatsClient = natsClient;
    protected readonly IServerConfig ServerConfig = serverConfig;
    protected bool IsStopping
    {
        get
        {
            lock (_handlerGate)
                return _stopping;
        }
    }

    protected void SubscribeWithHandler(string subject, Func<byte[], Task> handler)
    {
        NatsClient.Subscribe(subject, (_subject, msg) => RunTrackedHandler(() => handler(msg)));
    }

    protected void RunTrackedHandler(Func<Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        long handlerId;
        Task handlerTask;
        lock (_handlerGate)
        {
            if (_stopping)
                return;

            handlerId = ++_nextHandlerId;
            handlerTask = Task.Run(() => InvokeHandlerAsync(handler));
            _handlerTasks[handlerId] = handlerTask;
        }

        _ = RemoveCompletedHandlerAsync(handlerId, handlerTask);
    }

    protected void BeginStopping()
    {
        lock (_handlerGate)
        {
            if (_stopping)
                return;
            _stopping = true;
        }

        NatsClient.Close();
    }

    protected async Task DrainHandlersAsync()
    {
        Task[] pendingHandlers;
        lock (_handlerGate)
            pendingHandlers = _handlerTasks.Values.ToArray();

        await Task.WhenAll(pendingHandlers);
    }

    private async Task InvokeHandlerAsync(Func<Task> handler)
    {
        try
        {
            await handler();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Unhandled exception in handler");
        }
    }

    private async Task RemoveCompletedHandlerAsync(long handlerId, Task handlerTask)
    {
        await handlerTask;
        ((ICollection<KeyValuePair<long, Task>>)_handlerTasks)
            .Remove(new KeyValuePair<long, Task>(handlerId, handlerTask));
    }

    protected abstract void BroadcastPacket(string key, IPacket packet);
}
