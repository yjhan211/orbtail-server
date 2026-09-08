using game_server.matches;
using network.infrastructure;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NATS.Client;
using network.infrastructure.messaging;
using network.infrastructure.redis;
using user_server.matching;

namespace demo_regression_tests;

public sealed class NatsClientTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("65536")]
    [InlineData("2147483648")]
    public void UserServerRejectsInvalidServicePort(string? value)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["servicePort"] = value }).Build();
        Assert.Throws<InvalidOperationException>(() => user_server.UserServer.ResolveServicePort(configuration));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7000)]
    [InlineData(32768)]
    [InlineData(65535)]
    public void UserServerAcceptsFullServicePortRange(int port)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["servicePort"] = port.ToString() }).Build();
        Assert.Equal(port, user_server.UserServer.ResolveServicePort(configuration));
    }

    [Fact]
    public async Task UserServerRejectsSessionWhileMatchingLoopIsStopping()
    {
        var (natsConnection, _) = RecordingConnectionProxy.Create();
        var client = new NatsClient(natsConnection, TimeSpan.FromMilliseconds(50));
        await using var provider = CreateUserServerServices(client).BuildServiceProvider();
        var server = provider.GetServices<IHostedService>().OfType<user_server.UserServer>().Single();
        var manager = provider.GetRequiredService<MatchingManager>();
        var network = provider.GetRequiredService<network.core.NetworkService>();
        var finishMatching = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        typeof(MatchingManager).GetField("_matchingLoopTask", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(manager, finishMatching.Task);

        using var socket = new System.Net.Sockets.Socket(
            System.Net.Sockets.AddressFamily.InterNetwork,
            System.Net.Sockets.SocketType.Stream,
            System.Net.Sockets.ProtocolType.Tcp);
        using var receiveArgs = new System.Net.Sockets.SocketAsyncEventArgs();
        using var sendArgs = new System.Net.Sockets.SocketAsyncEventArgs();
        var connection = new network.core.TcpConnection();
        connection.InitializeConnection(socket, receiveArgs, sendArgs,
            (current, _, _) =>
            {
                current.CloseTransport(_ => { });
                current.MarkClosePrepared();
            },
            current =>
            {
                current.NotifySessionClosed(_ => { });
                current.DetachEventArgs(out _, out _);
                current.MarkReleased();
            });

        Task shutdown = server.StopAsync(CancellationToken.None);
        try
        {
            Assert.False(shutdown.IsCompleted);
            Assert.Equal(0, (int)typeof(network.core.NetworkService)
                .GetField("_stopping", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(network)!);

            var session = typeof(user_server.UserServer)
                .GetMethod("CreateSession", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(server, [connection]);

            Assert.Null(session);
            Assert.True(connection.IsReleased);
        }
        finally
        {
            connection.Disconnect();
            finishMatching.TrySetResult();
            await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task StartupFailureAndStopSharePendingCleanup()
    {
        var (connection, proxy) = RecordingConnectionProxy.Create();
        var client = new NatsClient(connection, TimeSpan.FromMilliseconds(50));
        await using var provider = CreateUserServerServices(client).BuildServiceProvider();
        var server = provider.GetServices<IHostedService>().OfType<user_server.UserServer>().Single();
        var taskTracker = provider.GetRequiredService<BackgroundTaskTracker>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = taskTracker.ShutdownToken.Register(() => stopping.TrySetResult());
        Assert.True(taskTracker.TryRun(() => release.Task, "hold cleanup"));

        // servicePort 미설정으로 시작에 실패한 뒤 실제 종료 경로에 진입한다.
        Task startup = server.StartAsync(CancellationToken.None);
        try
        {
            await stopping.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Task stop = server.StopAsync(CancellationToken.None);
            Assert.Same(stop, server.StopAsync(CancellationToken.None));
            Assert.False(stop.IsCompleted);
            Assert.False(startup.IsCompleted);
            Assert.False(proxy.Closed);
        }
        finally
        {
            release.TrySetResult();
        }
        await server.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => startup);
        Assert.Contains("servicePort", error.Message);
        Assert.Equal(1, proxy.CloseCalls);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task DiDisposalClosesConnectionExactlyOnce(bool asynchronous, bool closeFirst)
    {
        var (connection, proxy) = RecordingConnectionProxy.Create();
        var client = new NatsClient(connection, TimeSpan.FromMilliseconds(50));
        var services = new ServiceCollection();
        services.AddSingleton<INatsClient>(_ => client);
        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<INatsClient>().Subscribe("test.events", (_, _) => { });
        if (closeFirst) await client.CloseAsync();

        if (asynchronous) await provider.DisposeAsync();
        else provider.Dispose();

        Assert.Equal(1, proxy.CloseCalls);
        Assert.Equal(1, proxy.UnsubscribeCalls);
    }

    [Fact]
    public async Task UserServerRegistrationsResolveAndShareMatchingServices()
    {
        var (connection, proxy) = RecordingConnectionProxy.Create();
        var client = new NatsClient(connection, TimeSpan.FromMilliseconds(50));
        var services = CreateUserServerServices(client);
        var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
        await using (provider)
        {
            Assert.Null(provider.GetService<ILogger>());
            Assert.NotNull(provider.GetRequiredService<ILogger<user_server.sessions.PlayerSession>>());
            var hosted = provider.GetServices<IHostedService>().ToArray();
            Assert.Collection(hosted,
                item => Assert.IsType<user_server.HealthCheckService>(item),
                item => Assert.IsType<user_server.UserServer>(item));
            var manager = provider.GetRequiredService<MatchingManager>();
            Assert.Same(manager, provider.GetRequiredService<IMatchingManager>());
            var taskTracker = provider.GetRequiredService<BackgroundTaskTracker>();

            // 매니저 종료는 공용 추적기를 닫지 않는다. UserServer가 종료를 소유한다.
            await manager.StopAsync();
            Assert.False(taskTracker.ShutdownToken.IsCancellationRequested);
            await hosted.OfType<user_server.UserServer>().Single().StopAsync(CancellationToken.None);
            Assert.True(taskTracker.ShutdownToken.IsCancellationRequested);
            Assert.False(taskTracker.TryRun(() => Task.CompletedTask, "after stop"));
        }
        Assert.Equal(1, proxy.CloseCalls);
    }

    [Fact]
    public async Task UserServerActivationFailureStillClosesCreatedNatsConnection()
    {
        var (connection, proxy) = RecordingConnectionProxy.Create();
        var client = new NatsClient(connection, TimeSpan.FromMilliseconds(50));
        var services = CreateUserServerServices(client);
        services.RemoveAll<MatchingManager>();
        services.AddSingleton<MatchingManager>(sp =>
        {
            Assert.Same(client, sp.GetRequiredService<INatsClient>());
            throw new InvalidOperationException("simulated activation failure");
        });
        await using (var provider = services.BuildServiceProvider())
        {
            var error = Assert.Throws<InvalidOperationException>(() =>
                provider.GetServices<IHostedService>().ToArray());
            Assert.Equal("simulated activation failure", error.Message);
            Assert.False(proxy.Closed);
        }
        Assert.Equal(1, proxy.CloseCalls);
    }

    [Fact]
    public async Task GameServerRegistrationsShareLifecycleAndCloseNatsOnce()
    {
        var (connection, proxy) = RecordingConnectionProxy.Create();
        var services = CreateGameServerServices(new NatsClient(connection, TimeSpan.FromMilliseconds(50)));
        await using (var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }))
        {
            var server = provider.GetRequiredService<game_server.GameServer>();
            var lifecycle = provider.GetRequiredService<game_server.matches.MatchingLifecycleService>();
            Assert.Same(lifecycle, server.GetMatchingLifecycle());
            await server.StopAsync(CancellationToken.None);
            Assert.Equal(1, proxy.CloseCalls);
        }
        Assert.Equal(1, proxy.CloseCalls);
    }

    [Fact]
    public async Task GameServerActivationFailureStillDisposesNats()
    {
        var (connection, proxy) = RecordingConnectionProxy.Create();
        var services = CreateGameServerServices(new NatsClient(connection, TimeSpan.FromMilliseconds(50)));
        services.RemoveAll<game_server.GameServer>();
        services.AddSingleton<game_server.GameServer>(sp =>
        {
            sp.GetRequiredService<game_server.matches.MatchingLifecycleService>();
            throw new InvalidOperationException("activation failed");
        });
        await using (var provider = services.BuildServiceProvider())
        {
            Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<game_server.GameServer>());
            Assert.False(proxy.Closed);
        }
        Assert.Equal(1, proxy.CloseCalls);
    }

    private static ServiceCollection CreateGameServerServices(NatsClient client)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["natsEndPoint"] = "nats://unused:4222",
            ["gameServerId"] = "test-game-node",
            ["GAME_SERVER_PUBLIC_HOST"] = "127.0.0.1"
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        game_server.Program.ConfigureServices(new HostBuilderContext(new Dictionary<object, object>())
        {
            Configuration = configuration,
            HostingEnvironment = new Microsoft.Extensions.Hosting.Internal.HostingEnvironment { EnvironmentName = Environments.Production }
        }, services);
        services.RemoveAll<INatsClient>();
        services.AddSingleton<INatsClient>(_ => client);
        services.RemoveAll<IRedisOperations>();
        services.AddSingleton<IRedisOperations>(_ => new InMemoryRedisOperations());
        return services;
    }

    private static ServiceCollection CreateUserServerServices(NatsClient client)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["natsEndPoint"] = "nats://unused:4222",
                ["USER_SERVER_ID"] = "test-user-node"
            }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        user_server.Program.ConfigureServices(new HostBuilderContext(new Dictionary<object, object>())
        {
            Configuration = configuration
        }, services);
        services.RemoveAll<INatsClient>();
        services.AddSingleton<INatsClient>(_ => client);
        services.RemoveAll<IRedisOperations>();
        services.AddSingleton<IRedisOperations>(_ => new InMemoryRedisOperations());
        services.RemoveAll<IRedLockFactory>();
        services.AddSingleton<IRedLockFactory>(_ => new FakeRedLockFactory());
        return services;
    }

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
        public int CloseCalls { get; private set; }
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
                    CloseCalls++;
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
