using Microsoft.Extensions.Hosting;
using Orleans;
using Orleans.Configuration;
using Orleans.Concurrency;
using Orleans.Hosting;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.WireProtocol;

// <custom_serializer_registration_usings>
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.Cloning;

// </custom_serializer_registration_usings>

namespace Documentation.Hosting.Consul
{
    internal static class ConsulSnippets
    {
        internal static async Task ConfigureSilo(string[] args)
        {
            // <configure_consul_silo>
var builder = Host.CreateApplicationBuilder(args);

var consulAddress = new Uri(
    builder.Configuration["Consul:Address"]
        ?? throw new InvalidOperationException("Consul:Address isn't configured."));
var consulToken = builder.Configuration["Consul:Token"];

builder.UseOrleans(siloBuilder =>
{
    siloBuilder
        .Configure<ClusterOptions>(options =>
        {
            options.ServiceId = "orders";
            options.ClusterId = "production";
        })
        .UseConsulSiloClustering(options =>
        {
            options.ConfigureConsulClient(consulAddress, consulToken);
            options.KvRootFolder = "orleans/orders";
        });
});

await builder.Build().RunAsync();
            // </configure_consul_silo>
        }

        internal static async Task ConfigureClient(string[] args)
        {
            // <configure_consul_client>
var builder = Host.CreateApplicationBuilder(args);

var consulAddress = new Uri(
    builder.Configuration["Consul:Address"]
        ?? throw new InvalidOperationException("Consul:Address isn't configured."));
var consulToken = builder.Configuration["Consul:Token"];

builder.UseOrleansClient(clientBuilder =>
{
    clientBuilder
        .Configure<ClusterOptions>(options =>
        {
            options.ServiceId = "orders";
            options.ClusterId = "production";
        })
        .UseConsulClientClustering(options =>
        {
            options.ConfigureConsulClient(consulAddress, consulToken);
            options.KvRootFolder = "orleans/orders";
        });
});

await builder.Build().RunAsync();
            // </configure_consul_client>
        }
    }
}

namespace Documentation.Hosting.SerializationConfiguration
{
    internal abstract class TriggerRule;

    internal static class TypeManifestSnippets
    {
        internal static void AllowType(ISiloBuilder siloBuilder)
        {
            // <allow_type>
siloBuilder.Services.AddSerializer(serializerBuilder =>
{
    serializerBuilder.AddJsonSerializer(
        isSupported: type => type.Namespace?.StartsWith("MyApp", StringComparison.Ordinal) == true);
    serializerBuilder.Configure(options =>
        options.AddAllowedType(typeof(TriggerRule)));
});
            // </allow_type>
        }

        internal static void AllowAssembly(ISiloBuilder siloBuilder)
        {
            // <allow_assembly>
siloBuilder.Services.AddSerializer(serializerBuilder =>
{
    serializerBuilder.Configure(options =>
        options.AddAllowedAssembly(typeof(TriggerRule).Assembly));
});
            // </allow_assembly>
        }
    }
}

namespace Documentation.Hosting.SerializationCustomization
{
    // <custom_serializer>
internal sealed class CustomOrleansSerializer :
    IGeneralizedCodec, IGeneralizedCopier, ITypeFilter
{
    void IFieldCodec.WriteField<TBufferWriter>(
        ref Writer<TBufferWriter> writer,
        uint fieldIdDelta,
        [System.Diagnostics.CodeAnalysis.AllowNull] Type expectedType,
        object? value) =>
        throw new NotImplementedException();

    object? IFieldCodec.ReadValue<TInput>(
        ref Reader<TInput> reader, Field field) =>
        throw new NotImplementedException();

    bool IGeneralizedCodec.IsSupportedType(Type type) =>
        throw new NotImplementedException();

    object? IDeepCopier.DeepCopy(object? input, CopyContext context) =>
        throw new NotImplementedException();

    bool IGeneralizedCopier.IsSupportedType(Type type) =>
        throw new NotImplementedException();

    bool? ITypeFilter.IsTypeAllowed(Type type) =>
        throw new NotImplementedException();

}
    // </custom_serializer>

}

namespace Documentation.Hosting.SerializationImmutability
{
    // <immutable_type>
[Immutable]
public class MyImmutableType
{
    public int MyValue { get; }

    public MyImmutableType(int value)
    {
        MyValue = value;
    }
}
    // </immutable_type>

    // <immutable_parameter>
public interface ISummerGrain : IGrain
{
  // `values` will not be copied.
  ValueTask<int> Sum([Immutable] List<int> values);
}
    // </immutable_parameter>

    // <immutable_members>
[GenerateSerializer]
public sealed class MyType
{
    [Id(0), Immutable]
    public List<int> ReferenceData { get; set; } = [];

    [Id(1)]
    public List<int> RunningTotals { get; set; } = [];
}
    // </immutable_members>

    internal interface IRequestProcessor
    {
        // <mutable_request>
Task<byte[]> ProcessRequest(byte[] request);
        // </mutable_request>

        // <immutable_request>
Task<Immutable<byte[]>> ProcessRequest(Immutable<byte[]> request);
        // </immutable_request>
    }

    internal static class ImmutableWrapperSnippets
    {
        internal static void CreateWrapper()
        {
            byte[] buffer = [];

            // <create_immutable>
Immutable<byte[]> immutable = new(buffer);
            // </create_immutable>
        }

        internal static void ReadWrapper(Immutable<byte[]> immutable)
        {
            // <read_immutable>
byte[] buffer = immutable.Value;
            // </read_immutable>
        }
    }
}
