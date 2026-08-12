namespace Documentation.Hosting.SerializationCustomization
{
    // <register_custom_serializer>
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.Cloning;

public static class SerializationHostingExtensions
{
    public static ISerializerBuilder AddCustomSerializer(
        this ISerializerBuilder builder)
    {
        var services = builder.Services;

        services.AddSingleton<CustomOrleansSerializer>();
        services.AddSingleton<IGeneralizedCodec, CustomOrleansSerializer>();
        services.AddSingleton<IGeneralizedCopier, CustomOrleansSerializer>();
        services.AddSingleton<ITypeFilter, CustomOrleansSerializer>();

        return builder;
    }
}
    // </register_custom_serializer>
}
