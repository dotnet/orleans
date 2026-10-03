using System;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;
using Orleans.Serialization.Activators;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Configuration;
using Orleans.Serialization.GeneratedCodeHelpers;
using Orleans.Serialization.Serializers;

namespace Orleans.NativeAotSmoke;

internal static class RpcConstructionContracts
{
    public static void DefaultGraphsRespectConstructorDependencies()
    {
        var calls = 0;
        var collection = new ServiceCollection().AddSingleton<RpcConstructorDependency>();
        collection.AddSerializerContext(new global::OrleansCodeGen.OrleansNativeAotSmoke.RpcResponseFactories());
        collection.Configure<TypeManifestOptions>(options =>
        {
            options.AddFieldCodec(typeof(global::OrleansCodeGen.Orleans.NativeAotSmoke.Codec_RpcActivatedValue), typeof(RpcActivatedValue));
            options.AddActivator(typeof(global::OrleansCodeGen.Orleans.NativeAotSmoke.Activator_RpcActivatedValue), typeof(RpcActivatedValue));
            options.AddDefaultSerializerService<IActivator<RpcActivatedValue>, global::OrleansCodeGen.Orleans.NativeAotSmoke.Activator_RpcActivatedValue>(
                provider => new global::OrleansCodeGen.Orleans.NativeAotSmoke.Activator_RpcActivatedValue(
                    OrleansGeneratedCodeHelper.GetService<RpcConstructorDependency>(null!, provider)),
                dependencies: [typeof(RpcConstructorDependency)]);
            options.AddDefaultSerializerService<global::OrleansCodeGen.Orleans.NativeAotSmoke.Codec_RpcActivatedValue>(provider =>
            {
                calls++;
                return new(OrleansGeneratedCodeHelper.GetService<IActivator<RpcActivatedValue>>(null!, provider));
            });
            options.AddDefaultSerializerService<IFieldCodec<RpcActivatedValue>, global::OrleansCodeGen.Orleans.NativeAotSmoke.Codec_RpcActivatedValue>(
                provider => OrleansGeneratedCodeHelper.GetService<global::OrleansCodeGen.Orleans.NativeAotSmoke.Codec_RpcActivatedValue>(null!, provider),
                dependencies: [typeof(IActivator<RpcActivatedValue>)]);
            options.AddAllowedType(typeof(RpcActivatedValue));
        });
        using var services = collection.BuildServiceProvider();
        var provider = services.GetRequiredService<CodecProvider>();
        var codec = provider.GetCodec<RpcActivatedValue>();
        if (calls != 0 || codec is not global::OrleansCodeGen.Orleans.NativeAotSmoke.Codec_RpcActivatedValue
            || !ReferenceEquals(codec, provider.GetCodec<RpcActivatedValue>()))
            throw new InvalidOperationException("The inferred graph must decline arbitrary DI before canonical generated codec activation.");
        var serializer = services.GetRequiredService<Serializer>();
        var source = new RpcActivatedValue(services.GetRequiredService<RpcConstructorDependency>()) { Value = 59 };
        var result = serializer.Deserialize<RpcActivatedValue>(serializer.SerializeToArray(source));
        if (result is null || result.Value != 59 || result.ConstructionValue != 47 || calls != 0)
            throw new InvalidOperationException("The rooted generated codec and activator must construct through ordinary native metadata dispatch.");
    }
}

public sealed class RpcConstructorDependency
{
    public int Value => 47;
}

[GenerateSerializer, Immutable]
public sealed class RpcActivatedValue
{
    [GeneratedActivatorConstructor]
    public RpcActivatedValue(RpcConstructorDependency dependency) => ConstructionValue = dependency.Value;

    [NonSerialized]
    public readonly int ConstructionValue;

    [Id(0)]
    public int Value { get; set; }
}
