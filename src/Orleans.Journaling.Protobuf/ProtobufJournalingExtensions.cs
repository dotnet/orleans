using System.Buffers;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;

namespace Orleans.Journaling.Protobuf;

/// <summary>
/// Extension methods for configuring Protocol Buffers-based serialization for Orleans.Journaling.
/// </summary>
public static class ProtobufJournalingExtensions
{
    /// <summary>
    /// Configures Orleans.Journaling to use Google Protocol Buffers wire format for log entry serialization.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <returns>The silo builder for chaining.</returns>
    /// <remarks>
    /// <para>
    /// Each entry type is serialized using per-type codecs that use the protobuf wire format
    /// with <c>CodedOutputStream</c> and <c>CodedInputStream</c>. User values (keys, items)
    /// are serialized via <see cref="ILogDataCodec{T}"/> and embedded as length-delimited byte fields.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// builder.AddStateMachineStorage().UseProtobufCodec();
    /// </code>
    /// </example>
    public static ISiloBuilder UseProtobufCodec(this ISiloBuilder builder)
    {
        builder.Services.AddSingleton(typeof(ILogEntryCodec<>), typeof(ProtobufLogEntryCodecResolver<>));

        return builder;
    }
}

/// <summary>
/// Open-generic resolver that maps <c>ILogEntryCodec&lt;TEntry&gt;</c> to the appropriate Protocol Buffers codec.
/// </summary>
/// <remarks>
/// This class resolves the correct protobuf codec for the given entry type at construction time.
/// It uses <see cref="ActivatorUtilities"/> to create codec instances, allowing the DI container
/// to inject the required <see cref="ILogDataCodec{T}"/> dependencies for each type argument.
/// </remarks>
internal sealed class ProtobufLogEntryCodecResolver<TEntry>(IServiceProvider serviceProvider) : ILogEntryCodec<TEntry>
{
    private readonly ILogEntryCodec<TEntry> _inner = ResolveCodec(serviceProvider);

    private static ILogEntryCodec<TEntry> ResolveCodec(IServiceProvider sp)
    {
        var entryType = typeof(TEntry);

        if (!entryType.IsGenericType)
        {
            throw new NotSupportedException($"No Protobuf entry codec found for non-generic entry type '{entryType}'.");
        }

        var def = entryType.GetGenericTypeDefinition();
        var args = entryType.GetGenericArguments();

        var codecType =
            def == typeof(DurableDictionaryEntry<,>) ? typeof(ProtobufDictionaryEntryCodec<,>).MakeGenericType(args) :
            def == typeof(DurableListEntry<>) ? typeof(ProtobufListEntryCodec<>).MakeGenericType(args) :
            def == typeof(DurableQueueEntry<>) ? typeof(ProtobufQueueEntryCodec<>).MakeGenericType(args) :
            def == typeof(DurableSetEntry<>) ? typeof(ProtobufSetEntryCodec<>).MakeGenericType(args) :
            def == typeof(DurableValueEntry<>) ? typeof(ProtobufValueEntryCodec<>).MakeGenericType(args) :
            def == typeof(DurableStateEntry<>) ? typeof(ProtobufStateEntryCodec<>).MakeGenericType(args) :
            def == typeof(DurableTaskCompletionSourceEntry<>) ? typeof(ProtobufTcsEntryCodec<>).MakeGenericType(args) :
            throw new NotSupportedException($"No Protobuf entry codec found for entry type '{entryType}'.");

        return (ILogEntryCodec<TEntry>)ActivatorUtilities.CreateInstance(sp, codecType);
    }

    /// <inheritdoc/>
    public void Write(TEntry entry, IBufferWriter<byte> output) => _inner.Write(entry, output);

    /// <inheritdoc/>
    public TEntry Read(ReadOnlySequence<byte> input) => _inner.Read(input);
}
