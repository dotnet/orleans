using System;
using System.Diagnostics.CodeAnalysis;
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
    [return: NotNullIfNotNull(nameof(input))]
    public ByteString? DeepCopy(ByteString? input, CopyContext context)
    {
        if (context is null) throw new ArgumentNullException(nameof(context));

        if (input is null)
        {
            return null;
        }

        if (context.TryGetCopy<ByteString>(input, out var result))
        {
            return result!;
        }

        result = ByteString.CopyFrom(input.Span);
        context.RecordCopy(input, result);
        return result;
    }
}
