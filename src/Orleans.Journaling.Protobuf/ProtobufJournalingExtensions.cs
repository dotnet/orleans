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
    /// Each entry type is serialized as a generated protobuf message. User values are wrapped in
    /// <see cref="Messages.TypedValue"/> which uses native protobuf encoding for well-known types
    /// (scalars and <see cref="Google.Protobuf.IMessage"/>) and falls back to
    /// <see cref="ILogDataCodec{T}"/> for all other types.
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
/// <para>
/// This class resolves the correct protobuf codec for the given entry type at construction time.
/// For each type argument, it creates a <see cref="ProtobufValueConverter{T}"/> that uses native
/// protobuf encoding for well-known types and falls back to <see cref="ILogDataCodec{T}"/> only
/// when needed.
/// </para>
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

        // Build constructor arguments: one ProtobufValueConverter<T> per type argument.
        var converterArgs = new object[args.Length];
        for (var i = 0; i < args.Length; i++)
        {
            converterArgs[i] = CreateConverter(sp, args[i]);
        }

        return (ILogEntryCodec<TEntry>)Activator.CreateInstance(codecType, converterArgs)!;
    }

    private static object CreateConverter(IServiceProvider sp, Type valueType)
    {
        var converterType = typeof(ProtobufValueConverter<>).MakeGenericType(valueType);
        var isNative = (bool)converterType.GetProperty(nameof(ProtobufValueConverter<int>.IsNativeType))!.GetValue(null)!;

        if (isNative)
        {
            return Activator.CreateInstance(converterType)!;
        }

        // Resolve ILogDataCodec<T> from DI for the fallback path.
        var codecType = typeof(ILogDataCodec<>).MakeGenericType(valueType);
        var codec = sp.GetRequiredService(codecType);
        return Activator.CreateInstance(converterType, codec)!;
    }

    /// <inheritdoc/>
    public void Write(TEntry entry, IBufferWriter<byte> output) => _inner.Write(entry, output);

    /// <inheritdoc/>
    public TEntry Read(ReadOnlySequence<byte> input) => _inner.Read(input);
}
