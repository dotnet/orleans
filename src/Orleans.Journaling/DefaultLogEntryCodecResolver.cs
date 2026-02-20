using System.Buffers;
using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Journaling;

/// <summary>
/// Open-generic resolver for <see cref="ILogEntryCodec{TEntry}"/> that inspects the entry type
/// at construction time and delegates to the appropriate binary codec implementation.
/// </summary>
/// <remarks>
/// This class exists because the Microsoft DI container cannot decompose a composed generic
/// type parameter (e.g. <c>DurableDictionaryEntry&lt;K, V&gt;</c>) into its constituent type
/// arguments to close a multi-arity implementation type. Registering
/// <c>ILogEntryCodec&lt;&gt;</c> → <c>DefaultLogEntryCodecResolver&lt;&gt;</c> allows the
/// container to resolve any <c>ILogEntryCodec&lt;TEntry&gt;</c> by delegating to the correct
/// codec at runtime.
/// </remarks>
internal sealed class DefaultLogEntryCodecResolver<TEntry>(IServiceProvider serviceProvider) : ILogEntryCodec<TEntry>
{
    private readonly ILogEntryCodec<TEntry> _inner = ResolveCodec(serviceProvider);

    private static ILogEntryCodec<TEntry> ResolveCodec(IServiceProvider sp)
    {
        var entryType = typeof(TEntry);
        if (!entryType.IsGenericType)
        {
            throw new NotSupportedException($"No entry codec found for non-generic entry type '{entryType}'.");
        }

        var def = entryType.GetGenericTypeDefinition();
        var args = entryType.GetGenericArguments();

        var codecType =
            def == typeof(DurableDictionaryEntry<,>) ? typeof(OrleansBinaryDictionaryEntryCodec<,>).MakeGenericType(args) :
            def == typeof(DurableListEntry<>) ? typeof(OrleansBinaryListEntryCodec<>).MakeGenericType(args) :
            def == typeof(DurableQueueEntry<>) ? typeof(OrleansBinaryQueueEntryCodec<>).MakeGenericType(args) :
            def == typeof(DurableSetEntry<>) ? typeof(OrleansBinarySetEntryCodec<>).MakeGenericType(args) :
            def == typeof(DurableValueEntry<>) ? typeof(OrleansBinaryValueEntryCodec<>).MakeGenericType(args) :
            def == typeof(DurableStateEntry<>) ? typeof(OrleansBinaryStateEntryCodec<>).MakeGenericType(args) :
            def == typeof(DurableTaskCompletionSourceEntry<>) ? typeof(OrleansBinaryTcsEntryCodec<>).MakeGenericType(args) :
            throw new NotSupportedException($"No entry codec found for entry type '{entryType}'.");

        return (ILogEntryCodec<TEntry>)ActivatorUtilities.CreateInstance(sp, codecType);
    }

    /// <inheritdoc/>
    public void Write(TEntry entry, IBufferWriter<byte> output) => _inner.Write(entry, output);

    /// <inheritdoc/>
    public TEntry Read(ReadOnlySequence<byte> input) => _inner.Read(input);
}
