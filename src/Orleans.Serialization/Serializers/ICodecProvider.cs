using Orleans.Serialization.Cloning;
using System;

namespace Orleans.Serialization.Serializers
{
    public interface ICodecProvider :
        IFieldCodecProvider,
        IBaseCodecProvider,
        IValueSerializerProvider,
        IActivatorProvider,
        IDeepCopierProvider
    {
        IServiceProvider Services { get; }
    }
}