using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.Serialization;
using Orleans.Serialization.Configuration;

namespace Orleans.Docs.Snippets.Serialization;

public static class SerializationConfigurationExamples
{
    public static void ConfigureNewtonsoftJson(ISiloBuilder siloBuilder)
    {
        // <configure_newtonsoft_json>
        siloBuilder.Services.AddSerializer(serializerBuilder =>
        {
            serializerBuilder.AddNewtonsoftJsonSerializer(
                isSupported: type => type.Namespace?.StartsWith("Example.Namespace", StringComparison.Ordinal) is true);
        });
        // </configure_newtonsoft_json>
    }

    public static void ConfigureSystemTextJson(ISiloBuilder siloBuilder)
    {
        // <configure_system_text_json>
        siloBuilder.Services.AddSerializer(serializerBuilder =>
        {
            serializerBuilder.AddJsonSerializer(
                isSupported: type => type.Namespace?.StartsWith("Example.Namespace", StringComparison.Ordinal) is true);
        });
        // </configure_system_text_json>
    }

    public static void RegisterTypeNameFilter(ISiloBuilder siloBuilder)
    {
        // <register_type_name_filter>
        siloBuilder.Services.AddSingleton<ITypeNameFilter, ApplicationTypeNameFilter>();
        // </register_type_name_filter>
    }

    public static void AllowAllTypes(ISiloBuilder siloBuilder)
    {
        // <allow_all_types>
        siloBuilder.Services.AddSerializer(serializerBuilder =>
        {
            serializerBuilder.Configure((TypeManifestOptions options) =>
                options.AllowAllTypes = true);
        });
        // </allow_all_types>
    }
}

// <application_type_name_filter>
public sealed class ApplicationTypeNameFilter : ITypeNameFilter
{
    public bool? IsTypeNameAllowed(string typeName, string assemblyName)
    {
        if (assemblyName == "MyApp.Contracts"
            || assemblyName.StartsWith("MyApp.Contracts,", StringComparison.Ordinal))
        {
            return true;
        }

        return null;
    }
}
// </application_type_name_filter>
