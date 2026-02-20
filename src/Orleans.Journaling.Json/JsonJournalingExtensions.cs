using System.Buffers;
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

        // Register the shared JsonSerializerOptions.
        builder.Services.AddSingleton(options.SerializerOptions);

        // Replace the default codec resolver with the JSON codec resolver.
        builder.Services.AddSingleton(typeof(ILogEntryCodec<>), typeof(JsonLogEntryCodecResolver<>));

        return builder;
    }
}

/// <summary>
/// Open-generic resolver that maps <c>ILogEntryCodec&lt;TEntry&gt;</c> to the appropriate JSON codec.
/// </summary>
internal sealed class JsonLogEntryCodecResolver<TEntry>(IServiceProvider serviceProvider) : ILogEntryCodec<TEntry>
{
    private readonly ILogEntryCodec<TEntry> _inner = ResolveCodec(serviceProvider);

    private static ILogEntryCodec<TEntry> ResolveCodec(IServiceProvider sp)
    {
        var jsonOptions = sp.GetService<JsonSerializerOptions>() ?? JsonSerializerOptions.Default;
        var entryType = typeof(TEntry);

        if (!entryType.IsGenericType)
        {
            throw new NotSupportedException($"No JSON entry codec found for non-generic entry type '{entryType}'.");
        }

        var def = entryType.GetGenericTypeDefinition();
        var args = entryType.GetGenericArguments();

        var codecType =
            def == typeof(DurableDictionaryEntry<,>) ? typeof(JsonDictionaryEntryCodec<,>).MakeGenericType(args) :
            def == typeof(DurableListEntry<>) ? typeof(JsonListEntryCodec<>).MakeGenericType(args) :
            def == typeof(DurableQueueEntry<>) ? typeof(JsonQueueEntryCodec<>).MakeGenericType(args) :
            def == typeof(DurableSetEntry<>) ? typeof(JsonSetEntryCodec<>).MakeGenericType(args) :
            def == typeof(DurableValueEntry<>) ? typeof(JsonValueEntryCodec<>).MakeGenericType(args) :
            def == typeof(DurableStateEntry<>) ? typeof(JsonStateEntryCodec<>).MakeGenericType(args) :
            def == typeof(DurableTaskCompletionSourceEntry<>) ? typeof(JsonTcsEntryCodec<>).MakeGenericType(args) :
            throw new NotSupportedException($"No JSON entry codec found for entry type '{entryType}'.");

        return (ILogEntryCodec<TEntry>)Activator.CreateInstance(codecType, jsonOptions)!;
    }

    /// <inheritdoc/>
    public void Write(TEntry entry, IBufferWriter<byte> output) => _inner.Write(entry, output);

    /// <inheritdoc/>
    public TEntry Read(ReadOnlySequence<byte> input) => _inner.Read(input);
}
