using Orleans.Serialization.Codecs;
using System;

namespace Orleans.Serialization.Serializers
{
    public interface IGeneralizedCodec : IFieldCodec<object>
    {
        bool IsSupportedType(Type type);
    }

    public interface ISpecializableCodec
    {
        bool IsSupportedType(Type type);
        IFieldCodec GetSpecializedCodec(Type type);
    }
}