using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Orleans.Journaling.Json;
using Orleans.Providers;
using Orleans.Runtime;

namespace Orleans.Journaling;

/// <summary>
/// Provides extensions for configuring journaling services.
/// </summary>
public static class JournalingHostingExtensions
{
    /// <summary>
    /// Adds the services, durable state types, and journal formats required for journaling.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <returns>The silo builder.</returns>
    /// <remarks>
    /// Resolving the standard grain-scoped <see cref="IDurableStateManager"/> enrolls it in the grain lifecycle.
    /// Its registered durable states recover during <see cref="GrainLifecycleStage.SetupState"/>, before grain activation.
    /// Managers created through <see cref="IJournaledStateManagerFactory"/> have caller-owned initialization and disposal.
    /// </remarks>
    public static ISiloBuilder AddJournaling(this ISiloBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddOptions<JournaledStateManagerOptions>();
        builder.Services.TryAddSingleton(static serviceProvider =>
            serviceProvider.GetService<OrleansInstruments>() is { } instruments
                ? new JournalingInstruments(instruments)
                : JournalingInstruments.CreateForDirectConstruction());
        builder.Services.TryAddSingleton<JournaledStateManagerShared>();
        builder.Services.TryAddScoped<IJournaledStateManager>(static services =>
            new DurableStateManager(
                services.GetRequiredService<JournaledStateManagerShared>(),
                services.GetRequiredService<IJournalStorageProvider>(),
                services.GetRequiredService<IGrainContext>()));
        builder.Services.TryAddScoped<IDurableStateManager>(static services => (IDurableStateManager)services.GetRequiredService<IJournaledStateManager>());
        builder.Services.TryAddSingleton<IJournaledStateManagerFactory>(static services =>
            services.GetKeyedService<IJournaledStateManagerFactory>(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME)
                ?? ActivatorUtilities.CreateInstance<JournaledStateManagerFactory>(services));

        // Register JSON as the default format family and keep Orleans binary available for existing data.
        builder.Services.AddJsonJournalFormat(tryAdd: true);
        TryAddOrleansBinaryJournalingFormat(builder.Services);

        builder.Services.TryAddKeyedScoped(typeof(IDurableDictionary<,>), KeyedService.AnyKey, typeof(DurableDictionary<,>));
        builder.Services.TryAddKeyedScoped(typeof(IDurableList<>), KeyedService.AnyKey, typeof(DurableList<>));
        builder.Services.TryAddKeyedScoped(typeof(IDurableQueue<>), KeyedService.AnyKey, typeof(DurableQueue<>));
        builder.Services.TryAddKeyedScoped(typeof(IDurableSet<>), KeyedService.AnyKey, typeof(DurableSet<>));
        builder.Services.TryAddKeyedScoped(typeof(IDurableValue<>), KeyedService.AnyKey, typeof(DurableValue<>));
        builder.Services.TryAddKeyedScoped(typeof(IPersistentState<>), KeyedService.AnyKey, typeof(JournaledPersistentState<>));
        builder.Services.TryAddKeyedScoped(typeof(IDurableTaskCompletionSource<>), KeyedService.AnyKey, typeof(DurableTaskCompletionSource<>));
        return builder;
    }

    /// <summary>
    /// Registers a custom durable state contract and its state machine implementation.
    /// </summary>
    /// <typeparam name="TState">The application state contract.</typeparam>
    /// <typeparam name="TImplementation">The state machine implementation.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection.</returns>
    /// <remarks>
    /// Requires journaling services registered by <see cref="AddJournaling"/>.
    /// Constructor dependencies are resolved from the grain activation's service scope.
    /// Use the factory overload when construction needs the state name.
    /// The manager registers the instance; its constructor need not register itself.
    /// </remarks>
    public static IServiceCollection AddStateMachine<TState, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TImplementation>(
        this IServiceCollection services)
        where TState : class
        where TImplementation : class, TState, IStateMachine
    {
        ArgumentNullException.ThrowIfNull(services);
        var factory = ActivatorUtilities.CreateFactory<TImplementation>([]);
        return services.AddStateMachine<TState, TImplementation>((serviceProvider, _) => factory(serviceProvider, null));
    }

    /// <summary>
    /// Registers a factory for a custom durable state contract.
    /// </summary>
    /// <typeparam name="TState">The application state contract.</typeparam>
    /// <typeparam name="TImplementation">The state machine implementation.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="factory">Creates a state component using its activation's service scope and stable state name.</param>
    /// <returns>The service collection.</returns>
    /// <remarks>
    /// Requires journaling services registered by <see cref="AddJournaling"/>.
    /// Keyed injection and the manager return the same named instance. The manager registers the state,
    /// and the activation scope disposes it. The factory is not invoked for an existing compatible state.
    /// </remarks>
    public static IServiceCollection AddStateMachine<TState, TImplementation>(
        this IServiceCollection services,
        Func<IServiceProvider, string, TImplementation> factory)
        where TState : class
        where TImplementation : class, TState, IStateMachine
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(factory);
        services.AddKeyedScoped<TState>(KeyedService.AnyKey, (serviceProvider, key) =>
        {
            if (key is not string name || string.IsNullOrEmpty(name))
            {
                throw new ArgumentException("A durable state service key must be a non-empty string.", nameof(key));
            }

            if (serviceProvider.GetRequiredService<IDurableStateManager>() is not DurableStateManager manager)
            {
                throw new InvalidOperationException("Custom state machine factories require the grain's durable state manager registered by AddJournaling.");
            }

            return manager.GetOrAddState<TState, TImplementation>(name, factory);
        });
        return services;
    }

    /// <summary>
    /// Registers a named journal storage provider and its journaled state manager factory.
    /// </summary>
    /// <typeparam name="TProvider">The provider implementation type.</typeparam>
    /// <param name="builder">The silo builder.</param>
    /// <param name="name">The provider name.</param>
    /// <param name="factory">The factory which creates the singleton provider.</param>
    /// <returns>The silo builder.</returns>
    /// <remarks>
    /// If <typeparamref name="TProvider"/> implements <see cref="IJournalStorageCatalog"/> or
    /// <see cref="ILifecycleParticipant{TLifecycleObservable}"/>, those services are registered for the same provider.
    /// All providers use the shared journal format configuration. The
    /// <see cref="ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME"/> binding also supplies the unkeyed services.
    /// The default binding honors unkeyed singleton <see cref="IJournalStorageProvider"/> customizations. A replacement
    /// provider must implement the catalog and lifecycle contracts declared by <typeparamref name="TProvider"/>.
    /// Repeating a registration with the same name and implementation type retains the original factory.
    /// </remarks>
    /// <exception cref="ArgumentNullException">The builder or factory is null.</exception>
    /// <exception cref="ArgumentException">The provider name is empty or whitespace.</exception>
    /// <exception cref="InvalidOperationException">The name is already registered with another provider type.</exception>
    public static ISiloBuilder AddJournalStorage<TProvider>(
        this ISiloBuilder builder,
        string name,
        Func<IServiceProvider, TProvider> factory)
        where TProvider : class, IJournalStorageProvider
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(factory);

        var services = builder.Services;
        foreach (var descriptor in services)
        {
            if (descriptor.ServiceType == typeof(JournalStorageRegistration)
                && descriptor.ImplementationInstance is JournalStorageRegistration registration
                && string.Equals(registration.Name, name, StringComparison.Ordinal))
            {
                if (registration.ProviderType != typeof(TProvider))
                {
                    throw new InvalidOperationException(
                        $"Journal storage provider '{name}' is already registered with implementation type " +
                        $"'{registration.ProviderType.FullName}' and cannot be registered as '{typeof(TProvider).FullName}'.");
                }

                return builder;
            }
        }

        if (services.Any(descriptor => descriptor.IsKeyedService
            && Equals(descriptor.ServiceKey, name)
            && (descriptor.ServiceType == typeof(IJournalStorageProvider)
                || descriptor.ServiceType == typeof(IJournalStorageCatalog)
                || descriptor.ServiceType == typeof(IJournaledStateManagerFactory))))
        {
            throw new InvalidOperationException($"Journal storage provider '{name}' already has keyed services registered.");
        }

        builder.AddJournaling();
        services.AddSingleton(new JournalStorageRegistration(name, typeof(TProvider)));
        var isDefault = string.Equals(name, ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, StringComparison.Ordinal);
        if (isDefault)
        {
            services.TryAddSingleton(factory);
            services.TryAddSingleton<IJournalStorageProvider>(static serviceProvider => serviceProvider.GetRequiredService<TProvider>());
            services.AddKeyedSingleton<IJournalStorageProvider>(name, static (serviceProvider, _) =>
                serviceProvider.GetRequiredService<IJournalStorageProvider>());
        }
        else
        {
            services.AddKeyedSingleton<IJournalStorageProvider>(name, (serviceProvider, _) => factory(serviceProvider));
        }

        services.AddKeyedSingleton<IJournaledStateManagerFactory>(name, (serviceProvider, _) =>
            new JournaledStateManagerFactory(
                serviceProvider.GetRequiredService<JournaledStateManagerShared>(),
                serviceProvider.GetRequiredKeyedService<IJournalStorageProvider>(name)));

        var supportsCatalog = typeof(IJournalStorageCatalog).IsAssignableFrom(typeof(TProvider));
        if (supportsCatalog)
        {
            services.AddKeyedSingleton<IJournalStorageCatalog>(name, (serviceProvider, _) =>
                (IJournalStorageCatalog)serviceProvider.GetRequiredKeyedService<IJournalStorageProvider>(name));
        }

        if (typeof(ILifecycleParticipant<ISiloLifecycle>).IsAssignableFrom(typeof(TProvider)))
        {
            services.AddSingleton(serviceProvider =>
                (ILifecycleParticipant<ISiloLifecycle>)serviceProvider.GetRequiredKeyedService<IJournalStorageProvider>(name));
        }

        if (isDefault && supportsCatalog)
        {
            services.TryAddSingleton<IJournalStorageCatalog>(serviceProvider =>
                serviceProvider.GetRequiredKeyedService<IJournalStorageCatalog>(name));
        }

        return builder;
    }

    /// <summary>
    /// Registers volatile in-memory journal storage as the default provider.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <returns>The silo builder.</returns>
    public static ISiloBuilder AddVolatileJournalStorage(this ISiloBuilder builder)
        => builder.AddVolatileJournalStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME);

    /// <summary>
    /// Registers an independent named volatile in-memory journal storage provider.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <param name="name">The provider name.</param>
    /// <returns>The silo builder.</returns>
    public static ISiloBuilder AddVolatileJournalStorage(this ISiloBuilder builder, string name)
        => builder.AddJournalStorage(name, static services => ActivatorUtilities.CreateInstance<VolatileJournalStorageProvider>(services));

    private sealed record JournalStorageRegistration(string Name, Type ProviderType);

    internal static OptionsBuilder<TOptions> AddJournalStorageOptions<TOptions>(this IServiceCollection services, string name)
        where TOptions : class
        => services.AddOptions<TOptions>(
            string.Equals(name, ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, StringComparison.Ordinal)
                ? Options.DefaultName
                : name);

    internal static IOptions<TOptions> GetJournalStorageOptions<TOptions>(this IServiceProvider services, string name)
        where TOptions : class
        => string.Equals(name, ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, StringComparison.Ordinal)
            ? services.GetRequiredService<IOptions<TOptions>>()
            : Options.Create(services.GetRequiredService<IOptionsMonitor<TOptions>>().Get(name));

    private static void TryAddOrleansBinaryJournalingFormat(IServiceCollection services)
    {
        var key = JournalFormatServices.ValidateJournalFormatKey(OrleansBinaryJournalFormat.JournalFormatKey);

        services.TryAddSingleton<OrleansBinaryJournalFormat>();
        services.TryAddKeyedSingleton<IJournalFormat>(key, static (sp, _) => sp.GetRequiredService<OrleansBinaryJournalFormat>());
        services.TryAddSingleton<IJournalFormat>(static sp => sp.GetRequiredService<OrleansBinaryJournalFormat>());

        services.TryAddKeyedSingleton(typeof(IDurableDictionaryCommandCodec<,>), key, typeof(OrleansBinaryDurableDictionaryCommandCodec<,>));
        services.TryAddKeyedSingleton(typeof(IDurableListCommandCodec<>), key, typeof(OrleansBinaryDurableListCommandCodec<>));
        services.TryAddKeyedSingleton(typeof(IDurableQueueCommandCodec<>), key, typeof(OrleansBinaryDurableQueueCommandCodec<>));
        services.TryAddKeyedSingleton(typeof(IDurableSetCommandCodec<>), key, typeof(OrleansBinaryDurableSetCommandCodec<>));
        services.TryAddKeyedSingleton(typeof(IDurableValueCommandCodec<>), key, typeof(OrleansBinaryDurableValueCommandCodec<>));
        services.TryAddKeyedSingleton(typeof(IPersistentStateCommandCodec<>), key, typeof(OrleansBinaryPersistentStateCommandCodec<>));
        services.TryAddKeyedSingleton(typeof(IDurableTaskCompletionSourceCommandCodec<>), key, typeof(OrleansBinaryDurableTaskCompletionSourceCommandCodec<>));
    }
}
