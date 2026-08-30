using network.contracts.scaling;
using network.infrastructure;

namespace user_server.services.scaling;

public sealed class MatchingGameServerRoutingOptions
{
    public bool Enabled { get; init; }
    public TimeSpan MaximumNodeAge { get; init; } = TimeSpan.FromSeconds(12);
    public TimeSpan ReservationLifetime { get; init; } = TimeSpan.FromMinutes(5);

    public void Validate()
    {
        if (!Enabled)
            return;

        if (MaximumNodeAge <= TimeSpan.Zero)
            throw new InvalidOperationException(
                "horizontalScaling:nodeLeaseSeconds must be greater than zero.");
        if (ReservationLifetime <= TimeSpan.Zero)
            throw new InvalidOperationException(
                "horizontalScaling:reservationSeconds must be greater than zero.");
    }
}

public sealed class MatchingManagerScalingContext
{
    public MatchingManagerScalingContext(
        UserServerClusterOptions userServerOptions,
        UserServerProcessIdentity identity,
        IUserServerCoordinationStore coordinationStore,
        IMatchingDeliveryRouter deliveryRouter,
        IGameServerRoutingStore gameServerRoutingStore,
        MatchingGameServerRoutingOptions gameServerRoutingOptions,
        MatchingLifecycleOutboxStore lifecycleOutboxStore)
    {
        ArgumentNullException.ThrowIfNull(userServerOptions);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(coordinationStore);
        ArgumentNullException.ThrowIfNull(deliveryRouter);
        ArgumentNullException.ThrowIfNull(gameServerRoutingStore);
        ArgumentNullException.ThrowIfNull(gameServerRoutingOptions);
        ArgumentNullException.ThrowIfNull(lifecycleOutboxStore);

        userServerOptions.Validate();
        gameServerRoutingOptions.Validate();
        if (!identity.IsValid)
            throw new ArgumentException("UserServer process identity is invalid.", nameof(identity));
        if (deliveryRouter.Identity != identity)
            throw new ArgumentException(
                "Matching delivery router identity must match the UserServer process identity.",
                nameof(deliveryRouter));

        UserServerOptions = userServerOptions;
        Identity = identity;
        CoordinationStore = coordinationStore;
        DeliveryRouter = deliveryRouter;
        GameServerRoutingStore = gameServerRoutingStore;
        GameServerRoutingOptions = gameServerRoutingOptions;
        LifecycleOutboxStore = lifecycleOutboxStore;
    }

    public UserServerClusterOptions UserServerOptions { get; }
    public UserServerProcessIdentity Identity { get; }
    public IUserServerCoordinationStore CoordinationStore { get; }
    public IMatchingDeliveryRouter DeliveryRouter { get; }
    public IGameServerRoutingStore GameServerRoutingStore { get; }
    public MatchingGameServerRoutingOptions GameServerRoutingOptions { get; }
    public MatchingLifecycleOutboxStore LifecycleOutboxStore { get; }

    public bool UserServerScalingEnabled => UserServerOptions.Enabled;
    public bool GameServerRoutingEnabled => GameServerRoutingOptions.Enabled;
}
