using Microsoft.Extensions.DependencyInjection;
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

public static class ServiceCollectionExtensions
{
    public static void AddUserServerServices(this IServiceCollection services,
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
    }

    private static void AddFactories(this IServiceCollection services)
    {
        services.AddSingleton<IPlayerFactory, PlayerFactory>();
    }

    private static void AddRepositories(this IServiceCollection services)
    {
        services.AddScoped<IPlayerRepository, PlayerRepository>();
        services.AddScoped<IQuestRepository, QuestRepository>();
        services.AddScoped<IInventoryRepository, InventoryRepository>();
        services.AddScoped<IMailRepository, MailRepository>();
    }

    private static void AddCommandHandlers(this IServiceCollection services,
        Func<long, GameSession?> getSessionFunc)
    {
        // Register PlayerCommandHandler for all command types
        services.AddSingleton<PlayerCommandHandler>(_ =>
            new PlayerCommandHandler(getSessionFunc));

        // Register as specific command handler interfaces
        services.AddSingleton<ICommandHandler<WearItemCommand>>(sp =>
            sp.GetRequiredService<PlayerCommandHandler>());
        services.AddSingleton<ICommandHandler<UseItemCommand>>(sp =>
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
    }

    private static void AddQueryHandlers(this IServiceCollection services,
        Func<long, GameSession?> getSessionFunc)
    {
        // Register PlayerQueryHandler
        services.AddSingleton<PlayerQueryHandler>(_ =>
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
    }

    private static void AddEventInfrastructure(this IServiceCollection services)
    {
        // Register event dispatcher as singleton
        services.AddSingleton<IEventDispatcher, EventDispatcher>();

        // Register event handlers
        services.AddSingleton<QuestCompletedEventHandler>();
        services.AddSingleton<QuestStartedEventHandler>();
        services.AddSingleton<PlayerInfoChangedEventHandler>();
    }

    private static void AddPresentationHandlers(this IServiceCollection services)
    {
        services.AddSingleton<PlayerProtocolHandler>();
    }
}
