using System.Buffers;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Hosting;

namespace Orleans.Journaling.Json;

/// <summary>
/// Options for configuring JSON Lines-based journaling.
/// </summary>
public sealed class JsonJournalOptions
{
    /// <summary>
    /// Gets or sets the <see cref="JsonSerializerOptions"/> used for serialization.
    /// </summary>
    /// <remarks>
    /// Durable entry codecs resolve <see cref="JsonTypeInfo{T}"/>
    /// metadata for each journaled payload type from this options instance. Configure
    /// <see cref="JsonSerializerOptions.TypeInfoResolver"/> or <see cref="JsonSerializerOptions.TypeInfoResolverChain"/>
    /// with source-generated metadata for journaled value, key, and state types when trimming or using Native AOT.
    /// </remarks>
    public JsonSerializerOptions SerializerOptions { get; set; } = new JsonSerializerOptions(JsonSerializerDefaults.General);

    /// <summary>
    /// Adds source-generated JSON metadata to the serializer options used for journaled payload values.
    /// </summary>
    /// <param name="typeInfoResolver">The resolver, typically a generated <see cref="System.Text.Json.Serialization.JsonSerializerContext"/> instance.</param>
    /// <returns>This options instance for chaining.</returns>
    /// <remarks>
    /// Use this method to register metadata for every journaled key, value, and state type when trimming or using Native AOT.
    /// </remarks>
    /// <example>
    /// <code>
    /// builder.AddJournalStorage().UseJsonJournalFormat(options =>
    /// {
    ///     options.AddTypeInfoResolver(MyJournalJsonContext.Default);
    /// });
    /// </code>
    /// </example>
    public JsonJournalOptions AddTypeInfoResolver(IJsonTypeInfoResolver typeInfoResolver)
    {
        ArgumentNullException.ThrowIfNull(typeInfoResolver);

        SerializerOptions.TypeInfoResolverChain.Add(typeInfoResolver);
        return this;
    }
}

/// <summary>
/// Extension methods for configuring the JSON Lines Orleans.Journaling format.
/// </summary>
public static class JsonJournalExtensions
{
    /// <summary>
    /// The well-known key for the JSON Lines journal format.
    /// </summary>
    public const string JournalFormatKey = "json";

    /// <summary>
    /// Configures this silo with the JSON Lines Orleans.Journaling format family.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <param name="configure">Optional delegate to configure <see cref="JsonJournalOptions"/>.</param>
    /// <returns>The silo builder for chaining.</returns>
    /// <example>
    /// <code>
    /// builder.AddJournalStorage().UseJsonJournalFormat();
    ///
    /// builder.AddJournalStorage().UseJsonJournalFormat(MyJournalJsonContext.Default);
    ///
    /// builder.AddJournalStorage().UseJsonJournalFormat(options =>
    /// {
    ///     options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    ///     options.AddTypeInfoResolver(MyJournalJsonContext.Default);
    /// });
    /// </code>
    /// </example>
    public static ISiloBuilder UseJsonJournalFormat(this ISiloBuilder builder, Action<JsonJournalOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new JsonJournalOptions();
        configure?.Invoke(options);

        // Capture the serializer options directly — avoid registering a bare
        // JsonSerializerOptions singleton that could collide with other components.
        var jsonOptions = options.SerializerOptions;

        builder.Services.AddJsonJournalFormat(jsonOptions, tryAdd: false);

        return builder;
    }

    /// <summary>
    /// Configures this silo with the JSON Lines Orleans.Journaling format family and registers source-generated metadata for journaled payload values.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <param name="typeInfoResolver">The resolver, typically a generated <see cref="System.Text.Json.Serialization.JsonSerializerContext"/> instance.</param>
    /// <returns>The silo builder for chaining.</returns>
    /// <remarks>
    /// This overload is the recommended low-friction entry point for trimming and Native AOT. The supplied resolver
    /// must include every journaled key, value, and state type.
    /// </remarks>
    /// <example>
    /// <code>
    /// builder.AddJournalStorage().UseJsonJournalFormat(MyJournalJsonContext.Default);
    /// </code>
    /// </example>
    public static ISiloBuilder UseJsonJournalFormat(this ISiloBuilder builder, IJsonTypeInfoResolver typeInfoResolver)
    {
        ArgumentNullException.ThrowIfNull(typeInfoResolver);

        return builder.UseJsonJournalFormat(options => options.AddTypeInfoResolver(typeInfoResolver));
    }

    internal static IServiceCollection AddJsonJournalFormat(this IServiceCollection services, JsonSerializerOptions jsonOptions, bool tryAdd)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(jsonOptions);

        var key = JournalFormatServices.ValidateJournalFormatKey(JournalFormatKey);
        if (tryAdd)
        {
            services.TryAddSingleton<JsonLinesJournalFormat>();
            services.TryAddKeyedSingleton<IJournalFormat>(key, static (sp, _) => sp.GetRequiredService<JsonLinesJournalFormat>());
            services.TryAddSingleton<IJournalFormat>(static sp => sp.GetRequiredService<JsonLinesJournalFormat>());

            services.TryAddSingleton<JsonOperationCodecProvider>(_ => new JsonOperationCodecProvider(jsonOptions));
            services.TryAddKeyedSingleton<IDurableDictionaryOperationCodecProvider>(key, static (sp, _) => sp.GetRequiredService<JsonOperationCodecProvider>());
            services.TryAddKeyedSingleton<IDurableListOperationCodecProvider>(key, static (sp, _) => sp.GetRequiredService<JsonOperationCodecProvider>());
            services.TryAddKeyedSingleton<IDurableQueueOperationCodecProvider>(key, static (sp, _) => sp.GetRequiredService<JsonOperationCodecProvider>());
            services.TryAddKeyedSingleton<IDurableSetOperationCodecProvider>(key, static (sp, _) => sp.GetRequiredService<JsonOperationCodecProvider>());
            services.TryAddKeyedSingleton<IDurableValueOperationCodecProvider>(key, static (sp, _) => sp.GetRequiredService<JsonOperationCodecProvider>());
            services.TryAddKeyedSingleton<IDurableStateOperationCodecProvider>(key, static (sp, _) => sp.GetRequiredService<JsonOperationCodecProvider>());
            services.TryAddKeyedSingleton<IDurableTaskCompletionSourceOperationCodecProvider>(key, static (sp, _) => sp.GetRequiredService<JsonOperationCodecProvider>());
            services.TryAddSingleton<IDurableDictionaryOperationCodecProvider>(static sp => sp.GetRequiredService<JsonOperationCodecProvider>());
            services.TryAddSingleton<IDurableListOperationCodecProvider>(static sp => sp.GetRequiredService<JsonOperationCodecProvider>());
            services.TryAddSingleton<IDurableQueueOperationCodecProvider>(static sp => sp.GetRequiredService<JsonOperationCodecProvider>());
            services.TryAddSingleton<IDurableSetOperationCodecProvider>(static sp => sp.GetRequiredService<JsonOperationCodecProvider>());
            services.TryAddSingleton<IDurableValueOperationCodecProvider>(static sp => sp.GetRequiredService<JsonOperationCodecProvider>());
            services.TryAddSingleton<IDurableStateOperationCodecProvider>(static sp => sp.GetRequiredService<JsonOperationCodecProvider>());
            services.TryAddSingleton<IDurableTaskCompletionSourceOperationCodecProvider>(static sp => sp.GetRequiredService<JsonOperationCodecProvider>());
        }
        else
        {
            services.AddSingleton<JsonLinesJournalFormat>();
            services.AddKeyedSingleton<IJournalFormat>(key, static (sp, _) => sp.GetRequiredService<JsonLinesJournalFormat>());
            services.AddSingleton<IJournalFormat>(static sp => sp.GetRequiredService<JsonLinesJournalFormat>());

            services.AddSingleton<JsonOperationCodecProvider>(_ => new JsonOperationCodecProvider(jsonOptions));
            services.AddKeyedSingleton<IDurableDictionaryOperationCodecProvider>(key, static (sp, _) => sp.GetRequiredService<JsonOperationCodecProvider>());
            services.AddKeyedSingleton<IDurableListOperationCodecProvider>(key, static (sp, _) => sp.GetRequiredService<JsonOperationCodecProvider>());
            services.AddKeyedSingleton<IDurableQueueOperationCodecProvider>(key, static (sp, _) => sp.GetRequiredService<JsonOperationCodecProvider>());
            services.AddKeyedSingleton<IDurableSetOperationCodecProvider>(key, static (sp, _) => sp.GetRequiredService<JsonOperationCodecProvider>());
            services.AddKeyedSingleton<IDurableValueOperationCodecProvider>(key, static (sp, _) => sp.GetRequiredService<JsonOperationCodecProvider>());
            services.AddKeyedSingleton<IDurableStateOperationCodecProvider>(key, static (sp, _) => sp.GetRequiredService<JsonOperationCodecProvider>());
            services.AddKeyedSingleton<IDurableTaskCompletionSourceOperationCodecProvider>(key, static (sp, _) => sp.GetRequiredService<JsonOperationCodecProvider>());
            services.AddSingleton<IDurableDictionaryOperationCodecProvider>(static sp => sp.GetRequiredService<JsonOperationCodecProvider>());
            services.AddSingleton<IDurableListOperationCodecProvider>(static sp => sp.GetRequiredService<JsonOperationCodecProvider>());
            services.AddSingleton<IDurableQueueOperationCodecProvider>(static sp => sp.GetRequiredService<JsonOperationCodecProvider>());
            services.AddSingleton<IDurableSetOperationCodecProvider>(static sp => sp.GetRequiredService<JsonOperationCodecProvider>());
            services.AddSingleton<IDurableValueOperationCodecProvider>(static sp => sp.GetRequiredService<JsonOperationCodecProvider>());
            services.AddSingleton<IDurableStateOperationCodecProvider>(static sp => sp.GetRequiredService<JsonOperationCodecProvider>());
            services.AddSingleton<IDurableTaskCompletionSourceOperationCodecProvider>(static sp => sp.GetRequiredService<JsonOperationCodecProvider>());
        }

        return services;
    }
}

/// <summary>
/// JSON format implementation of the durable type codec providers.
/// </summary>
/// <remarks>
/// Each <c>GetCodec</c> method constructs the appropriate JSON codec using <c>new</c> and
/// the shared <see cref="JsonSerializerOptions"/> — no reflection required. Codec instances
/// are cached per closed generic combination so they behave as singletons.
/// </remarks>
internal sealed class JsonOperationCodecProvider(JsonSerializerOptions jsonOptions) :
    IDurableDictionaryOperationCodecProvider,
    IDurableListOperationCodecProvider,
    IDurableQueueOperationCodecProvider,
    IDurableSetOperationCodecProvider,
    IDurableValueOperationCodecProvider,
    IDurableStateOperationCodecProvider,
    IDurableTaskCompletionSourceOperationCodecProvider
{
    private readonly ConcurrentDictionary<Type, object> _codecs = new();

    /// <inheritdoc/>
    public IDurableDictionaryOperationCodec<TKey, TValue> GetCodec<TKey, TValue>() where TKey : notnull
        => (IDurableDictionaryOperationCodec<TKey, TValue>)_codecs.GetOrAdd(
            typeof(IDurableDictionaryOperationCodec<TKey, TValue>),
            _ => new JsonDictionaryOperationCodec<TKey, TValue>(jsonOptions));

    /// <inheritdoc/>
    public IDurableListOperationCodec<T> GetCodec<T>()
        => (IDurableListOperationCodec<T>)_codecs.GetOrAdd(
            typeof(IDurableListOperationCodec<T>),
            _ => new JsonListOperationCodec<T>(jsonOptions));

    /// <inheritdoc/>
    IDurableQueueOperationCodec<T> IDurableQueueOperationCodecProvider.GetCodec<T>()
        => (IDurableQueueOperationCodec<T>)_codecs.GetOrAdd(
            typeof(IDurableQueueOperationCodec<T>),
            _ => new JsonQueueOperationCodec<T>(jsonOptions));

    /// <inheritdoc/>
    IDurableSetOperationCodec<T> IDurableSetOperationCodecProvider.GetCodec<T>()
        => (IDurableSetOperationCodec<T>)_codecs.GetOrAdd(
            typeof(IDurableSetOperationCodec<T>),
            _ => new JsonSetOperationCodec<T>(jsonOptions));

    /// <inheritdoc/>
    IDurableValueOperationCodec<T> IDurableValueOperationCodecProvider.GetCodec<T>()
        => (IDurableValueOperationCodec<T>)_codecs.GetOrAdd(
            typeof(IDurableValueOperationCodec<T>),
            _ => new JsonValueOperationCodec<T>(jsonOptions));

    /// <inheritdoc/>
    IDurableStateOperationCodec<T> IDurableStateOperationCodecProvider.GetCodec<T>()
        => (IDurableStateOperationCodec<T>)_codecs.GetOrAdd(
            typeof(IDurableStateOperationCodec<T>),
            _ => new JsonStateOperationCodec<T>(jsonOptions));

    /// <inheritdoc/>
    IDurableTaskCompletionSourceOperationCodec<T> IDurableTaskCompletionSourceOperationCodecProvider.GetCodec<T>()
        => (IDurableTaskCompletionSourceOperationCodec<T>)_codecs.GetOrAdd(
            typeof(IDurableTaskCompletionSourceOperationCodec<T>),
            _ => new JsonTcsOperationCodec<T>(jsonOptions));
}
