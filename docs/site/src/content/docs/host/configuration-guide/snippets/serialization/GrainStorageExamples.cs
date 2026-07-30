using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Storage;

namespace Orleans.Docs.Snippets.Serialization;

// Define a placeholder class for the custom serializer example
public class MyCustomSerializer : IGrainStorageSerializer
{
    public BinaryData Serialize<T>(T value) => throw new NotImplementedException();
    public T Deserialize<T>(BinaryData data) => throw new NotImplementedException();
}

public static class GrainStorageConfiguration
{
    // <grain_storage_serializer_config>
    public static void ConfigureGrainStorageSerializer(ISiloBuilder siloBuilder)
    {
        siloBuilder.AddAzureBlobGrainStorage(
            "MyGrainStorage",
            (OptionsBuilder<AzureBlobStorageOptions> optionsBuilder) =>
            {
                optionsBuilder.Configure<MyCustomSerializer>(
                    (options, serializer) => options.GrainStorageSerializer = serializer);
            });
    }
    // </grain_storage_serializer_config>
}
