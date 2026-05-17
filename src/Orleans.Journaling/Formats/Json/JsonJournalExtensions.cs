using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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

    private readonly HashSet<Type> _polymorphicTypes = [];

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

    /// <summary>
    /// Configures explicit polymorphic JSON serialization for a journaled payload base type.
    /// </summary>
    /// <typeparam name="TBase">The base type used by the journaled payload, such as an event base type or <see cref="object"/>.</typeparam>
    /// <param name="typeDiscriminatorPropertyName">The JSON property name used to store the event type discriminator.</param>
    /// <returns>A builder used to register the derived payload types which can be written and read.</returns>
    /// <remarks>
    /// JSON journaling only deserializes explicitly registered derived types. Register a source-generated
    /// <see cref="System.Text.Json.Serialization.JsonSerializerContext"/> using <see cref="AddTypeInfoResolver"/>
    /// when trimming or using Native AOT.
    /// </remarks>
    /// <example>
    /// <code>
    /// builder.AddJournalStorage().UseJsonJournalFormat(options =>
    /// {
    ///     options.AddTypeInfoResolver(MyJournalJsonContext.Default);
    ///     options.ConfigurePolymorphicType&lt;object&gt;()
    ///         .AddDerivedType&lt;OrderCreated&gt;("order-created")
    ///         .AddDerivedType&lt;OrderShipped&gt;("order-shipped");
    /// });
    /// </code>
    /// </example>
    public JsonPolymorphicTypeBuilder<TBase> ConfigurePolymorphicType<TBase>(string typeDiscriminatorPropertyName = "$type")
        where TBase : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(typeDiscriminatorPropertyName);

        if (!_polymorphicTypes.Add(typeof(TBase)))
        {
            throw new InvalidOperationException($"Polymorphic JSON journaling is already configured for payload base type '{typeof(TBase).FullName}'.");
        }

        var registry = new JsonPolymorphicTypeRegistry<TBase>(typeDiscriminatorPropertyName);
        SerializerOptions.Converters.Add(new JsonPolymorphicTypeConverter<TBase>(registry));
        return new JsonPolymorphicTypeBuilder<TBase>(registry);
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
        var options = new JsonJournalOptions { SerializerOptions = jsonOptions };
        if (tryAdd)
        {
            services.TryAddSingleton(options);
            services.TryAddSingleton<JsonLinesJournalFormat>();
            services.TryAddKeyedSingleton<IJournalFormat>(key, static (sp, _) => sp.GetRequiredService<JsonLinesJournalFormat>());
            services.TryAddSingleton<IJournalFormat>(static sp => sp.GetRequiredService<JsonLinesJournalFormat>());

            services.TryAddKeyedSingleton(typeof(IDurableDictionaryCommandCodec<,>), key, typeof(JsonDurableDictionaryCommandCodecService<,>));
            services.TryAddKeyedSingleton(typeof(IDurableListCommandCodec<>), key, typeof(JsonDurableListCommandCodecService<>));
            services.TryAddKeyedSingleton(typeof(IDurableQueueCommandCodec<>), key, typeof(JsonDurableQueueCommandCodecService<>));
            services.TryAddKeyedSingleton(typeof(IDurableSetCommandCodec<>), key, typeof(JsonDurableSetCommandCodecService<>));
            services.TryAddKeyedSingleton(typeof(IDurableValueCommandCodec<>), key, typeof(JsonDurableValueCommandCodecService<>));
            services.TryAddKeyedSingleton(typeof(IPersistentStateCommandCodec<>), key, typeof(JsonPersistentStateCommandCodecService<>));
            services.TryAddKeyedSingleton(typeof(IDurableTaskCompletionSourceCommandCodec<>), key, typeof(JsonDurableTaskCompletionSourceCommandCodecService<>));
        }
        else
        {
            services.Replace(ServiceDescriptor.Singleton(options));
            services.AddSingleton<JsonLinesJournalFormat>();
            services.AddKeyedSingleton<IJournalFormat>(key, static (sp, _) => sp.GetRequiredService<JsonLinesJournalFormat>());
            services.AddSingleton<IJournalFormat>(static sp => sp.GetRequiredService<JsonLinesJournalFormat>());

            services.AddKeyedSingleton(typeof(IDurableDictionaryCommandCodec<,>), key, typeof(JsonDurableDictionaryCommandCodecService<,>));
            services.AddKeyedSingleton(typeof(IDurableListCommandCodec<>), key, typeof(JsonDurableListCommandCodecService<>));
            services.AddKeyedSingleton(typeof(IDurableQueueCommandCodec<>), key, typeof(JsonDurableQueueCommandCodecService<>));
            services.AddKeyedSingleton(typeof(IDurableSetCommandCodec<>), key, typeof(JsonDurableSetCommandCodecService<>));
            services.AddKeyedSingleton(typeof(IDurableValueCommandCodec<>), key, typeof(JsonDurableValueCommandCodecService<>));
            services.AddKeyedSingleton(typeof(IPersistentStateCommandCodec<>), key, typeof(JsonPersistentStateCommandCodecService<>));
            services.AddKeyedSingleton(typeof(IDurableTaskCompletionSourceCommandCodec<>), key, typeof(JsonDurableTaskCompletionSourceCommandCodecService<>));
        }

        return services;
    }
}
