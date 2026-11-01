using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Serialization;

namespace Documentation.SerializerContexts;

#region serializer_context_link
[SerializerContextImport<ApplicationSerializerContext>]
[OrleansSerializable<ApplicationEnvelope>]
public partial class ConsumerSerializerContext : SerializerContext;

[GenerateSerializer]
public sealed class ApplicationEnvelope
{
    [Id(0)]
    public List<DocumentationPayload<int>> Items { get; set; } = [];
}
#endregion

public static class ConsumerSerializerContextExample
{
    public static ApplicationEnvelope SerializeAndCopy()
    {
        #region serializer_context_link_usage
        using var services = new ServiceCollection()
            .AddSerializerContext(new ConsumerSerializerContext())
            .BuildServiceProvider();

        var serializer = services.GetRequiredService<Serializer>();
        var copier = services.GetRequiredService<DeepCopier>();
        var input = new ApplicationEnvelope
        {
            Items = [new DocumentationPayload<int> { Value = 13 }]
        };
        var restored = serializer.Deserialize<ApplicationEnvelope>(serializer.SerializeToArray(input))!;
        var copy = copier.Copy(restored)!;
        #endregion

        if (ReferenceEquals(restored.Items, copy.Items))
        {
            throw new InvalidOperationException("The copied envelope owns an independent item list.");
        }

        return copy;
    }
}
