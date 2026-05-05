using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Journaling.Protobuf;

/// <summary>
/// Options for configuring Protocol Buffers-based serialization for Orleans.Journaling.
/// </summary>
/// <remarks>
/// <para>
/// Common scalar values (<see cref="string"/>, byte arrays, numeric primitives, and <see cref="bool"/>)
/// are encoded natively by default. Google Protocol Buffers message values are encoded natively only
/// when their generated <see cref="MessageParser{T}"/> is registered explicitly. Other unregistered
/// values fall back to <see cref="ILogValueCodec{T}"/>.
/// </para>
/// </remarks>
public sealed class ProtobufJournalingOptions
{
    private readonly List<Action<IServiceCollection>> _configureServices = [];

    /// <summary>
    /// Registers a generated Protocol Buffers message parser for native value payload encoding.
    /// </summary>
    /// <typeparam name="T">The generated Protocol Buffers message type.</typeparam>
    /// <param name="parser">The generated parser, typically the message type's static <c>Parser</c> property.</param>
    /// <returns>This options instance for chaining.</returns>
    /// <remarks>
    /// <para>
    /// Registering a parser lets journaling encode <typeparamref name="T"/> values directly as protobuf
    /// message payloads without reflection. If a parser is not registered, values of this type use the
    /// configured <see cref="ILogValueCodec{T}"/> fallback.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// builder.AddLogStorage().UseProtobufJournalingFormat(options =>
    /// {
    ///     options.AddMessageParser(MyMessage.Parser);
    /// });
    /// </code>
    /// </example>
    public ProtobufJournalingOptions AddMessageParser<T>(MessageParser<T> parser)
        where T : IMessage<T>
    {
        ArgumentNullException.ThrowIfNull(parser);

        _configureServices.Add(services => services.AddSingleton<IProtobufValueCodec<T>>(new ProtobufMessageValueCodec<T>(parser)));
        return this;
    }

    internal void Apply(IServiceCollection services)
    {
        foreach (var configure in _configureServices)
        {
            configure(services);
        }
    }
}
