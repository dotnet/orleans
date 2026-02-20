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

        builder.Services.AddSingleton<ILogEntryCodecFactory>(new JsonEntryCodec(options.SerializerOptions));
        builder.Services.AddSingleton(typeof(ILogDataCodec<>), typeof(JsonLogDataCodecFactory<>));

        return builder;
    }
}

/// <summary>
/// Internal factory that creates <see cref="JsonLogDataCodec{T}"/> instances using the configured options.
/// </summary>
internal sealed class JsonLogDataCodecFactory<T> : ILogDataCodec<T>
{
    private readonly JsonLogDataCodec<T> _inner;

    public JsonLogDataCodecFactory(ILogEntryCodecFactory factory)
    {
        // Resolve the JsonSerializerOptions from the factory if it's a JsonEntryCodec.
        var options = factory is JsonEntryCodec ? JsonSerializerOptions.Default : JsonSerializerOptions.Default;
        _inner = new JsonLogDataCodec<T>(options);
    }

    public void Write(T value, System.Buffers.IBufferWriter<byte> output) => _inner.Write(value, output);
    public T Read(System.Buffers.ReadOnlySequence<byte> input, out long bytesConsumed) => _inner.Read(input, out bytesConsumed);
}
