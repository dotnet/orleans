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
    /// builder.AddLogStorage().UseJsonCodec(options =>
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
    /// The well-known key for the JSON Lines log format.
    /// </summary>
    public const string LogFormatKey = "json";

    /// <summary>
    /// Configures Orleans.Journaling to use JSON Lines for physical log segments and System.Text.Json for durable log entries.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <param name="configure">Optional delegate to configure <see cref="JsonJournalingOptions"/>.</param>
    /// <returns>The silo builder for chaining.</returns>
    /// <example>
    /// <code>
    /// builder.AddLogStorage().UseJsonCodec();
    ///
    /// builder.AddLogStorage().UseJsonCodec(MyJournalJsonContext.Default);
    ///
    /// builder.AddLogStorage().UseJsonCodec(options =>
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

        // Replace the default segment and operation codec providers with JSON implementations.
        builder.Services
            .AddJournalingFormatFamily(LogFormatKey)
            .AddLogFormat<JsonLinesLogFormat>()
            .AddOperationCodecProvider<JsonOperationCodecProvider>(_ => new JsonOperationCodecProvider(jsonOptions));

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
    /// builder.AddLogStorage().UseJsonCodec(MyJournalJsonContext.Default);
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
