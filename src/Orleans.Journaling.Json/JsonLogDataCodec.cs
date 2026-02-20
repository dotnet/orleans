using System.Buffers;
using System.Text.Json;

namespace Orleans.Journaling.Json;

/// <summary>
/// An <see cref="ILogDataCodec{T}"/> implementation that uses <see cref="System.Text.Json"/> for serialization.
/// </summary>
/// <typeparam name="T">The type of value to serialize and deserialize.</typeparam>
/// <remarks>
/// <para>
/// This codec serializes values as JSON using <see cref="JsonSerializer"/>. It is registered
/// as an open-generic service when <see cref="JsonJournalingExtensions.UseJsonCodec"/> is called.
/// </para>
/// <example>
/// <code>
/// // Use JSON for all durable state machine serialization
/// builder.AddStateMachineStorage().UseJsonCodec();
///
/// // Customize JSON options
/// builder.AddStateMachineStorage().UseJsonCodec(options =>
/// {
///     options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
/// });
/// </code>
/// </example>
/// </remarks>
public sealed class JsonLogDataCodec<T>(JsonSerializerOptions? options = null) : ILogDataCodec<T>
{
    private readonly JsonSerializerOptions _options = options ?? JsonSerializerOptions.Default;

    /// <inheritdoc/>
    public void Write(T value, IBufferWriter<byte> output)
    {
        using var writer = new Utf8JsonWriter(output);
        JsonSerializer.Serialize(writer, value, _options);
    }

    /// <inheritdoc/>
    public T Read(ReadOnlySequence<byte> input, out long bytesConsumed)
    {
        var reader = new Utf8JsonReader(input);
        var result = JsonSerializer.Deserialize<T>(ref reader, _options)!;
        bytesConsumed = reader.BytesConsumed;
        return result;
    }
}
