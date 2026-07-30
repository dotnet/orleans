using MessagePack;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans;
using Orleans.Serialization;
using System.Reflection;

namespace Orleans.Docs.Snippets.Serialization;

// <messagepack_type_definition>
[MessagePackObject]
public class OrderMessage
{
    [Key(0)]
    public string OrderId { get; set; }

    [Key(1)]
    public decimal Amount { get; set; }

    [Key(2)]
    public DateTime CreatedAt { get; set; }

    [Key(3)]
    public List<string> Items { get; set; }
}
// </messagepack_type_definition>

// <messagepack_grain_interface>
public interface IOrderGrain : IGrainWithStringKey
{
    Task<OrderMessage> GetOrder();
    Task PlaceOrder(OrderMessage order);
}
// </messagepack_grain_interface>

public static class MessagePackConfiguration
{
    // <messagepack_basic_config>
    public static void ConfigureMessagePackBasic(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        builder.UseOrleans(siloBuilder =>
        {
            siloBuilder.UseLocalhostClustering();
            siloBuilder.Services.AddSerializer(serializerBuilder => serializerBuilder.AddMessagePackSerializer(
                isSerializable: type => type.Namespace?.StartsWith("MyApp.Messages") == true,
                isCopyable: type => false,
                messagePackSerializerOptions: null
            ));
        });
    }
    // </messagepack_basic_config>

    // <messagepack_with_options>
    public static void ConfigureMessagePackWithOptions(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        builder.UseOrleans(siloBuilder =>
        {
            siloBuilder.UseLocalhostClustering();
            siloBuilder.Services.AddSerializer(serializerBuilder => serializerBuilder.AddMessagePackSerializer(
                isSerializable: type => type.GetCustomAttribute<MessagePackObjectAttribute>() != null,
                isCopyable: type => false,
                configureOptions: options => options.Configure(opts =>
                {
                    opts.SerializerOptions = MessagePackSerializerOptions.Standard
                        .WithCompression(MessagePackCompression.Lz4BlockArray);
                    opts.AllowDataContractAttributes = true;
                })
            ));
        });
    }
    // </messagepack_with_options>
}
