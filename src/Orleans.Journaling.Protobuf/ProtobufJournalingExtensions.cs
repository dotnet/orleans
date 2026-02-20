using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;

namespace Orleans.Journaling.Protobuf;

/// <summary>
/// Extension methods for configuring Protocol Buffers-based serialization for Orleans.Journaling.
/// </summary>
public static class ProtobufJournalingExtensions
{
    /// <summary>
    /// Configures Orleans.Journaling to use Google Protocol Buffers for log entry serialization.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <returns>The silo builder for chaining.</returns>
    /// <remarks>
    /// <para>
    /// When using Protocol Buffers, all value types used in durable state machines must implement
    /// <see cref="Google.Protobuf.IMessage{T}"/>, meaning they must be protobuf-generated types.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// builder.AddStateMachineStorage().UseProtobufCodec();
    /// </code>
    /// </example>
    public static ISiloBuilder UseProtobufCodec(this ISiloBuilder builder)
    {
        builder.Services.AddSingleton<ILogEntryCodecFactory, ProtobufEntryCodec>();
        builder.Services.AddSingleton(typeof(ILogDataCodec<>), typeof(ProtobufLogDataCodec<>));

        return builder;
    }
}
