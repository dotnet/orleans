using Google.Protobuf;
using Orleans.Serialization.Cloning;

namespace Orleans.Serialization;

/// <summary>
/// Copier for <see cref="ByteString"/>.
/// </summary>
[RegisterCopier]
public sealed class ByteStringCopier : IDeepCopier<ByteString>
{
    /// <inheritdoc/>
    public ByteString DeepCopy(ByteString input, CopyContext context)
    {
        if (context.TryGetCopy<ByteString>(input, out var result))
        {
            return result;
        }

        result = ByteString.CopyFrom(input.Span);
        context.RecordCopy(input, result);
        return result;
    }
}