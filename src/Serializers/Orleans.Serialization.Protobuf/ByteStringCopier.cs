using Google.Protobuf;
using Orleans.Serialization.Cloning;

namespace Orleans.Serialization;

[RegisterCopier]
public sealed class ByteStringCopier : IDeepCopier<ByteString>
{
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