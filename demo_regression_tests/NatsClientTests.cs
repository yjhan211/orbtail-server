using System.Reflection;
using Microsoft.Extensions.Logging;
using NATS.Client;
using network.infrastructure.messaging;

namespace demo_regression_tests;

public sealed class NatsClientTests
{
    [Fact]
    public void SubscribeLogsHandlerFailure()
    {
        (IConnection connection, RecordingConnectionProxy proxy) = RecordingConnectionProxy.Create();
        var logger = new RecordingLogger();
        var client = new NatsClient(connection, TimeSpan.FromMilliseconds(50), logger);
        client.Subscribe("test.events", (_, _) => throw new InvalidOperationException("handler failed"));

        proxy.Deliver("test.events", [1]);

        Assert.True(logger.Contains(LogLevel.Error, "NATS subscription handler failed"));
        client.Close();
    }

    [Fact]
    public async Task CloseAsyncCancelsRequestHandlerAfterGracePeriod()
    {
        (IConnection connection, RecordingConnectionProxy proxy) = RecordingConnectionProxy.Create();
        var logger = new RecordingLogger();
        var client = new NatsClient(connection, TimeSpan.FromMilliseconds(50), logger);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.SubscribeRequest("test.request", async (_, _, cancellationToken) =>
        {
            started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                cancellationObserved.TrySetResult();
                throw;
            }

            return null;
        });

        proxy.Deliver("test.request", [1]);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(1));

        await client.CloseAsync().WaitAsync(TimeSpan.FromSeconds(2));

        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(proxy.Closed);
        Assert.Equal(1, proxy.UnsubscribeCalls);
        Assert.True(logger.Contains(LogLevel.Warning, "exceeded the graceful shutdown period"));
    }

    [Fact]
    public async Task CloseAsyncClosesConnectionWhenHandlerIgnoresCancellation()
    {
        (IConnection connection, RecordingConnectionProxy proxy) = RecordingConnectionProxy.Create();
        var logger = new RecordingLogger();
        var client = new NatsClient(connection, TimeSpan.FromMilliseconds(50), logger);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.SubscribeRequest("test.request", async (_, _, _) =>
        {
            started.TrySetResult();
            try
            {
                await release.Task;
                return null;
            }
            finally
            {
                finished.TrySetResult();
            }
        });

        proxy.Deliver("test.request", [1]);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(1));

        await client.CloseAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(proxy.Closed);
        Assert.True(logger.Contains(LogLevel.Warning, "did not stop after cancellation"));
        release.TrySetResult();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    private class RecordingConnectionProxy : DispatchProxy
    {
        private EventHandler<MsgHandlerEventArgs>? _handler;
        private readonly List<RecordingSubscriptionProxy> _subscriptions = [];

        public bool Closed { get; private set; }
        public int UnsubscribeCalls => _subscriptions.Sum(subscription => subscription.UnsubscribeCalls);

        public static (IConnection Connection, RecordingConnectionProxy Proxy) Create()
        {
            IConnection connection = DispatchProxy.Create<IConnection, RecordingConnectionProxy>();
            return (connection, (RecordingConnectionProxy)connection);
        }

        public void Deliver(string subject, byte[] body)
        {
            var message = new Msg(subject, "_INBOX.test", body);
            var args = (MsgHandlerEventArgs?)Activator.CreateInstance(
                typeof(MsgHandlerEventArgs),
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                [message],
                null) ?? throw new InvalidOperationException("Could not create NATS message event args.");
            _handler?.Invoke(this, args);
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            args ??= [];
            switch (targetMethod.Name)
            {
                case nameof(IConnection.SubscribeAsync):
                    {
                        _handler = args.OfType<EventHandler<MsgHandlerEventArgs>>().Single();
                        IAsyncSubscription subscription = RecordingSubscriptionProxy.Create((string)args[0]!, out var proxy);
                        _subscriptions.Add(proxy);
                        return subscription;
                    }
                case nameof(IConnection.Close):
                    Closed = true;
                    return null;
                case nameof(IDisposable.Dispose):
                    Closed = true;
                    return null;
                default:
                    throw new NotSupportedException($"Unexpected IConnection call: {targetMethod.Name}");
            }
        }
    }

    private class RecordingSubscriptionProxy : DispatchProxy
    {
        private string _subject = string.Empty;

        public int UnsubscribeCalls { get; private set; }

        public static IAsyncSubscription Create(string subject, out RecordingSubscriptionProxy proxy)
        {
            IAsyncSubscription subscription = DispatchProxy.Create<IAsyncSubscription, RecordingSubscriptionProxy>();
            proxy = (RecordingSubscriptionProxy)subscription;
            proxy._subject = subject;
            return subscription;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            switch (targetMethod.Name)
            {
                case nameof(ISubscription.Unsubscribe):
                    UnsubscribeCalls++;
                    return null;
                case "get_Subject":
                    return _subject;
                case nameof(IDisposable.Dispose):
                    return null;
                default:
                    throw new NotSupportedException($"Unexpected IAsyncSubscription call: {targetMethod.Name}");
            }
        }
    }
}
