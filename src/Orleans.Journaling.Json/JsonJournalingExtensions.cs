using System.Buffers;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;

namespace Orleans.Journaling.Json;

/// <summary>
/// Options for configuring JSON Lines-based journaling.
/// </summary>
public sealed class JsonJournalingOptions
{
    /// <summary>
    /// Gets or sets the <see cref="System.Text.Json.JsonSerializerOptions"/> used for serialization.
    /// </summary>
    /// <remarks>
    /// Durable entry codecs resolve <see cref="System.Text.Json.Serialization.Metadata.JsonTypeInfo{T}"/>
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
    /// builder.AddStateMachineStorage().UseJsonCodec(options =>
    /// {
    ///     options.AddTypeInfoResolver(MyJournalJsonContext.Default);
    /// });
    /// </code>
    /// </example>
    public JsonJournalingOptions AddTypeInfoResolver(IJsonTypeInfoResolver typeInfoResolver)
    {
        ArgumentNullException.ThrowIfNull(typeInfoResolver);

        SerializerOptions.TypeInfoResolverChain.Add(typeInfoResolver);
        return this;
    }
}

/// <summary>
/// Extension methods for configuring JSON Lines-based serialization for Orleans.Journaling.
/// </summary>
public static class JsonJournalingExtensions
{
    /// <summary>
    /// Configures Orleans.Journaling to use JSON Lines for physical log extents and System.Text.Json for durable log entries.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <param name="configure">Optional delegate to configure <see cref="JsonJournalingOptions"/>.</param>
    /// <returns>The silo builder for chaining.</returns>
    /// <example>
    /// <code>
    /// builder.AddStateMachineStorage().UseJsonCodec();
    ///
    /// builder.AddStateMachineStorage().UseJsonCodec(MyJournalJsonContext.Default);
    ///
    /// builder.AddStateMachineStorage().UseJsonCodec(options =>
    /// {
    ///     options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    ///     options.AddTypeInfoResolver(MyJournalJsonContext.Default);
    /// });
    /// </code>
    /// </example>
    public static ISiloBuilder UseJsonCodec(this ISiloBuilder builder, Action<JsonJournalingOptions>? configure = null)
    {
        var options = new JsonJournalingOptions();
        configure?.Invoke(options);

        // Capture the serializer options directly — avoid registering a bare
        // JsonSerializerOptions singleton that could collide with other components.
        var jsonOptions = options.SerializerOptions;

        // Replace the default extent and entry codec providers with JSON implementations.
        builder.Services.AddSingleton<IStateMachineLogExtentCodec, JsonLinesLogExtentCodec>();
        builder.Services.AddSingleton<JsonLogEntryCodecProvider>(_ => new JsonLogEntryCodecProvider(jsonOptions));
        builder.Services.AddSingleton<IDurableDictionaryCodecProvider>(static sp => sp.GetRequiredService<JsonLogEntryCodecProvider>());
        builder.Services.AddSingleton<IDurableListCodecProvider>(static sp => sp.GetRequiredService<JsonLogEntryCodecProvider>());
        builder.Services.AddSingleton<IDurableQueueCodecProvider>(static sp => sp.GetRequiredService<JsonLogEntryCodecProvider>());
        builder.Services.AddSingleton<IDurableSetCodecProvider>(static sp => sp.GetRequiredService<JsonLogEntryCodecProvider>());
        builder.Services.AddSingleton<IDurableValueCodecProvider>(static sp => sp.GetRequiredService<JsonLogEntryCodecProvider>());
        builder.Services.AddSingleton<IDurableStateCodecProvider>(static sp => sp.GetRequiredService<JsonLogEntryCodecProvider>());
        builder.Services.AddSingleton<IDurableTaskCompletionSourceCodecProvider>(static sp => sp.GetRequiredService<JsonLogEntryCodecProvider>());

        return builder;
    }

    /// <summary>
    /// Configures Orleans.Journaling to use JSON Lines and registers source-generated metadata for journaled payload values.
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
    /// builder.AddStateMachineStorage().UseJsonCodec(MyJournalJsonContext.Default);
    /// </code>
    /// </example>
    public static ISiloBuilder UseJsonCodec(this ISiloBuilder builder, IJsonTypeInfoResolver typeInfoResolver)
    {
        ArgumentNullException.ThrowIfNull(typeInfoResolver);

        return builder.UseJsonCodec(options => options.AddTypeInfoResolver(typeInfoResolver));
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
internal sealed class JsonLogEntryCodecProvider(JsonSerializerOptions jsonOptions) :
    IDurableDictionaryCodecProvider,
    IDurableListCodecProvider,
    IDurableQueueCodecProvider,
    IDurableSetCodecProvider,
    IDurableValueCodecProvider,
    IDurableStateCodecProvider,
    IDurableTaskCompletionSourceCodecProvider
{
    private readonly ConcurrentDictionary<Type, object> _codecs = new();

    /// <inheritdoc/>
    public IDurableDictionaryCodec<TKey, TValue> GetCodec<TKey, TValue>() where TKey : notnull
        => (IDurableDictionaryCodec<TKey, TValue>)_codecs.GetOrAdd(
            typeof(IDurableDictionaryCodec<TKey, TValue>),
            _ => new JsonDictionaryEntryCodec<TKey, TValue>(jsonOptions));

    /// <inheritdoc/>
    public IDurableListCodec<T> GetCodec<T>()
        => (IDurableListCodec<T>)_codecs.GetOrAdd(
            typeof(IDurableListCodec<T>),
            _ => new JsonListEntryCodec<T>(jsonOptions));

    /// <inheritdoc/>
    IDurableQueueCodec<T> IDurableQueueCodecProvider.GetCodec<T>()
        => (IDurableQueueCodec<T>)_codecs.GetOrAdd(
            typeof(IDurableQueueCodec<T>),
            _ => new JsonQueueEntryCodec<T>(jsonOptions));

    /// <inheritdoc/>
    IDurableSetCodec<T> IDurableSetCodecProvider.GetCodec<T>()
        => (IDurableSetCodec<T>)_codecs.GetOrAdd(
            typeof(IDurableSetCodec<T>),
            _ => new JsonSetEntryCodec<T>(jsonOptions));

    /// <inheritdoc/>
    IDurableValueCodec<T> IDurableValueCodecProvider.GetCodec<T>()
        => (IDurableValueCodec<T>)_codecs.GetOrAdd(
            typeof(IDurableValueCodec<T>),
            _ => new JsonValueEntryCodec<T>(jsonOptions));

    /// <inheritdoc/>
    IDurableStateCodec<T> IDurableStateCodecProvider.GetCodec<T>()
        => (IDurableStateCodec<T>)_codecs.GetOrAdd(
            typeof(IDurableStateCodec<T>),
            _ => new JsonStateEntryCodec<T>(jsonOptions));

    /// <inheritdoc/>
    IDurableTaskCompletionSourceCodec<T> IDurableTaskCompletionSourceCodecProvider.GetCodec<T>()
        => (IDurableTaskCompletionSourceCodec<T>)_codecs.GetOrAdd(
            typeof(IDurableTaskCompletionSourceCodec<T>),
            _ => new JsonTcsEntryCodec<T>(jsonOptions));
}
