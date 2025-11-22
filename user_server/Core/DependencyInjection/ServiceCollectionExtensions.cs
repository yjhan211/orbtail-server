using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using network.interfaces;
using user_server.application.commands;
using user_server.application.commands.handlers;
using user_server.application.commands.player;
using user_server.application.events;
using user_server.application.events.handlers;
using user_server.application.queries;
using user_server.application.queries.handlers;
using user_server.application.queries.player;
using user_server.domain.repositories;
using user_server.domain.factories;
using user_server.infrastructure.events;
using user_server.infrastructure.factories;
using user_server.infrastructure.network;
using user_server.infrastructure.repositories;
using user_server.presentation.handlers;

namespace user_server.core.dependencyinjection;

/// <summary>
/// Extension methods for setting up services in an IServiceCollection
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers all user server services
    /// </summary>
    public static IServiceCollection AddUserServerServices(
        this IServiceCollection services,
        ICacheHelper cacheHelper,
        Func<long, GameSession?> getSessionFunc)
    {
        // Register cache helper as singleton
        services.AddSingleton(cacheHelper);
        services.AddSingleton(getSessionFunc);

        // Register factories
        services.AddFactories();

        // Register repositories
        services.AddRepositories();

        // Register command handlers
        services.AddCommandHandlers(getSessionFunc);

        // Register query handlers
        services.AddQueryHandlers(getSessionFunc);

        // Register event infrastructure
        services.AddEventInfrastructure();

        // Register presentation handlers
        services.AddPresentationHandlers();

        return services;
    }

    private static IServiceCollection AddFactories(this IServiceCollection services)
    {
        services.AddSingleton<IPlayerFactory, PlayerFactory>();
        return services;
    }

    private static IServiceCollection AddRepositories(this IServiceCollection services)
    {
        services.AddScoped<IPlayerRepository, PlayerRepository>();
        services.AddScoped<IQuestRepository, QuestRepository>();
        services.AddScoped<IInventoryRepository, InventoryRepository>();
        services.AddScoped<IMailRepository, MailRepository>();

        return services;
    }

    private static IServiceCollection AddCommandHandlers(
        this IServiceCollection services,
        Func<long, GameSession?> getSessionFunc)
    {
        // Register PlayerCommandHandler for all command types
        services.AddSingleton<PlayerCommandHandler>(sp =>
            new PlayerCommandHandler(getSessionFunc));

        // Register as specific command handler interfaces
        services.AddSingleton<ICommandHandler<MoveCommand>>(sp =>
            sp.GetRequiredService<PlayerCommandHandler>());
        services.AddSingleton<ICommandHandler<WearItemCommand>>(sp =>
            sp.GetRequiredService<PlayerCommandHandler>());
        services.AddSingleton<ICommandHandler<UseItemCommand>>(sp =>
            sp.GetRequiredService<PlayerCommandHandler>());
        services.AddSingleton<ICommandHandler<ExploreCommand>>(sp =>
            sp.GetRequiredService<PlayerCommandHandler>());
        services.AddSingleton<ICommandHandler<IncreaseQuestCountCommand>>(sp =>
            sp.GetRequiredService<PlayerCommandHandler>());
        services.AddSingleton<ICommandHandler<CompleteQuestCommand>>(sp =>
            sp.GetRequiredService<PlayerCommandHandler>());
        services.AddSingleton<ICommandHandler<StartQuestCommand>>(sp =>
            sp.GetRequiredService<PlayerCommandHandler>());
        services.AddSingleton<ICommandHandler<SendMailCommand>>(sp =>
            sp.GetRequiredService<PlayerCommandHandler>());
        services.AddSingleton<ICommandHandler<ReceiveMailCommand>>(sp =>
            sp.GetRequiredService<PlayerCommandHandler>());
        services.AddSingleton<ICommandHandler<PerformSocialActionCommand>>(sp =>
            sp.GetRequiredService<PlayerCommandHandler>());
        services.AddSingleton<ICommandHandler<SetPlayerNameCommand>>(sp =>
            sp.GetRequiredService<PlayerCommandHandler>());
        services.AddSingleton<ICommandHandler<ChangeMapCommand>>(sp =>
            sp.GetRequiredService<PlayerCommandHandler>());
        services.AddSingleton<ICommandHandler<EnterMapCommand>>(sp =>
            sp.GetRequiredService<PlayerCommandHandler>());
        services.AddSingleton<ICommandHandler<EnterCampCommand>>(sp =>
            sp.GetRequiredService<PlayerCommandHandler>());

        return services;
    }

    private static IServiceCollection AddQueryHandlers(
        this IServiceCollection services,
        Func<long, GameSession?> getSessionFunc)
    {
        // Register PlayerQueryHandler
        services.AddSingleton<PlayerQueryHandler>(sp =>
            new PlayerQueryHandler(getSessionFunc));

        // Register as specific query handler interfaces
        services.AddSingleton<IQueryHandler<GetPlayerInfoQuery, PlayerInfoResult>>(sp =>
            sp.GetRequiredService<PlayerQueryHandler>());
        services.AddSingleton<IQueryHandler<GetPlayerQuestsQuery, PlayerQuestsResult>>(sp =>
            sp.GetRequiredService<PlayerQueryHandler>());
        services.AddSingleton<IQueryHandler<GetPlayerItemsQuery, PlayerItemsResult>>(sp =>
            sp.GetRequiredService<PlayerQueryHandler>());
        services.AddSingleton<IQueryHandler<GetPlayerMailsQuery, PlayerMailsResult>>(sp =>
            sp.GetRequiredService<PlayerQueryHandler>());

        return services;
    }

    private static IServiceCollection AddEventInfrastructure(this IServiceCollection services)
    {
        // Register event dispatcher as singleton
        services.AddSingleton<IEventDispatcher, EventDispatcher>();

        // Register event handlers
        services.AddSingleton<QuestCompletedEventHandler>();
        services.AddSingleton<QuestStartedEventHandler>();
        services.AddSingleton<PlayerInfoChangedEventHandler>();
        services.AddSingleton<PlayerDamagedEventHandler>();

        return services;
    }

    private static IServiceCollection AddPresentationHandlers(this IServiceCollection services)
    {
        services.AddSingleton<PlayerProtocolHandler>();

        return services;
    }

    /// <summary>
    /// Configures event handlers by registering them with the event dispatcher
    /// </summary>
    public static IServiceProvider ConfigureEventHandlers(this IServiceProvider serviceProvider)
    {
        var dispatcher = serviceProvider.GetRequiredService<IEventDispatcher>();

        // Register quest event handlers
        var questCompletedHandler = serviceProvider.GetRequiredService<QuestCompletedEventHandler>();
        var questStartedHandler = serviceProvider.GetRequiredService<QuestStartedEventHandler>();

        dispatcher.RegisterHandler(questCompletedHandler);
        dispatcher.RegisterHandler(questStartedHandler);

        // Register player event handlers
        var playerInfoChangedHandler = serviceProvider.GetRequiredService<PlayerInfoChangedEventHandler>();
        var playerDamagedHandler = serviceProvider.GetRequiredService<PlayerDamagedEventHandler>();

        dispatcher.RegisterHandler(playerInfoChangedHandler);
        dispatcher.RegisterHandler(playerDamagedHandler);

        return serviceProvider;
    }
}