using Orleans.Serialization.Codecs;
using System;

namespace Orleans.Serialization.Serializers
{
    public interface IFieldCodecProvider
    {
        IFieldCodec<TField> GetCodec<TField>();
        IFieldCodec<TField> TryGetCodec<TField>();
        IFieldCodec<object> GetCodec(Type fieldType);
        IFieldCodec<object> TryGetCodec(Type fieldType);
    }
}