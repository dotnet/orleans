using System.Buffers;
using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;

namespace Orleans.Journaling.Json;

/// <summary>
/// Options for configuring JSON-based log data serialization.
/// </summary>
public sealed class JsonJournalingOptions
{
    /// <summary>
    /// Gets or sets the <see cref="System.Text.Json.JsonSerializerOptions"/> used for serialization.
    /// </summary>
    public JsonSerializerOptions SerializerOptions { get; set; } = new JsonSerializerOptions(JsonSerializerDefaults.General);
}

/// <summary>
/// Extension methods for configuring JSON-based serialization for Orleans.Journaling.
/// </summary>
public static class JsonJournalingExtensions
{
    /// <summary>
    /// Configures Orleans.Journaling to use System.Text.Json for log entry serialization.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <param name="configure">Optional delegate to configure <see cref="JsonJournalingOptions"/>.</param>
    /// <returns>The silo builder for chaining.</returns>
    /// <example>
    /// <code>
    /// builder.AddStateMachineStorage().UseJsonCodec();
    ///
    /// builder.AddStateMachineStorage().UseJsonCodec(options =>
    /// {
    ///     options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
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

        // Replace the default codec providers with the JSON codec provider.
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
    public ILogEntryCodec<DurableDictionaryEntry<TKey, TValue>> GetCodec<TKey, TValue>() where TKey : notnull
        => (ILogEntryCodec<DurableDictionaryEntry<TKey, TValue>>)_codecs.GetOrAdd(
            typeof(DurableDictionaryEntry<TKey, TValue>),
            _ => new JsonDictionaryEntryCodec<TKey, TValue>(jsonOptions));

    /// <inheritdoc/>
    public ILogEntryCodec<DurableListEntry<T>> GetCodec<T>()
        => (ILogEntryCodec<DurableListEntry<T>>)_codecs.GetOrAdd(
            typeof(DurableListEntry<T>),
            _ => new JsonListEntryCodec<T>(jsonOptions));

    /// <inheritdoc/>
    ILogEntryCodec<DurableQueueEntry<T>> IDurableQueueCodecProvider.GetCodec<T>()
        => (ILogEntryCodec<DurableQueueEntry<T>>)_codecs.GetOrAdd(
            typeof(DurableQueueEntry<T>),
            _ => new JsonQueueEntryCodec<T>(jsonOptions));

    /// <inheritdoc/>
    ILogEntryCodec<DurableSetEntry<T>> IDurableSetCodecProvider.GetCodec<T>()
        => (ILogEntryCodec<DurableSetEntry<T>>)_codecs.GetOrAdd(
            typeof(DurableSetEntry<T>),
            _ => new JsonSetEntryCodec<T>(jsonOptions));

    /// <inheritdoc/>
    ILogEntryCodec<DurableValueEntry<T>> IDurableValueCodecProvider.GetCodec<T>()
        => (ILogEntryCodec<DurableValueEntry<T>>)_codecs.GetOrAdd(
            typeof(DurableValueEntry<T>),
            _ => new JsonValueEntryCodec<T>(jsonOptions));

    /// <inheritdoc/>
    ILogEntryCodec<DurableStateEntry<T>> IDurableStateCodecProvider.GetCodec<T>()
        => (ILogEntryCodec<DurableStateEntry<T>>)_codecs.GetOrAdd(
            typeof(DurableStateEntry<T>),
            _ => new JsonStateEntryCodec<T>(jsonOptions));

    /// <inheritdoc/>
    ILogEntryCodec<DurableTaskCompletionSourceEntry<T>> IDurableTaskCompletionSourceCodecProvider.GetCodec<T>()
        => (ILogEntryCodec<DurableTaskCompletionSourceEntry<T>>)_codecs.GetOrAdd(
            typeof(DurableTaskCompletionSourceEntry<T>),
            _ => new JsonTcsEntryCodec<T>(jsonOptions));
}
