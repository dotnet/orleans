using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Serialization;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.InterfaceCollectionSmoke;
using Orleans.Serialization.Serializers;

using var services = new ServiceCollection()
    .AddSerializerContext(new InterfaceCollectionSmokeContext())
    .AddSingleton<IGeneralizedCodec, InterfaceCollectionCodecResolver>()
    .BuildServiceProvider();
var resolver = services.GetRequiredService<IGeneralizedCodec>();
foreach (var (codec, expectedInterface) in InterfaceCollectionContracts.SupportedTypes)
{
    if (!InterfaceCollectionCodecHelpers.TryGetInterfaceTypeForCodecType(codec, out var actualInterface)
        || actualInterface != expectedInterface
        || !resolver.IsSupportedType(codec))
    {
        throw new InvalidOperationException($"Codec {codec} resolved to {actualInterface}, expected {expectedInterface}.");
    }
}

foreach (var type in InterfaceCollectionContracts.UnsupportedTypes)
{
    if (InterfaceCollectionCodecHelpers.TryGetInterfaceTypeForCodecType(type, out var actualInterface)
        || actualInterface is not null
        || (type is not null && resolver.IsSupportedType(type)))
    {
        throw new InvalidOperationException($"Unsupported codec {type} resolved to {actualInterface}.");
    }
}

Console.WriteLine("Interface collection codec metadata resolution passed: value, reference, nested, and rejected type shapes.");

[GenerateSerializerContext<int>]
internal partial class InterfaceCollectionSmokeContext : SerializerContext;
