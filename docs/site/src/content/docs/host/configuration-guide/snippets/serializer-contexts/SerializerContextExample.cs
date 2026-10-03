using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Serialization;

namespace Documentation.SerializerContexts;

#region serializer_context_declaration
[GenerateSerializerContext(typeof(List<Dictionary<string, int>>))]
[GenerateSerializerContext(typeof(DocumentationPayload<int>))]
public partial class ApplicationSerializerContext : SerializerContext;

[GenerateSerializer]
public sealed class DocumentationPayload<T>
{
    [Id(0)]
    public T Value { get; set; } = default!;

    [Id(1)]
    public List<Dictionary<string, int>> Items { get; set; } = [];
}

[GenerateSerializer]
public sealed class DocumentationPrimitivePayload
{
    [Id(0)]
    public int Value { get; set; }
}
#endregion

[GenerateSerializer(GenerateFieldIds = GenerateFieldIds.PublicProperties)]
public sealed class DocumentationImplicitPayload<T>
{
    public List<T> Values { get; set; } = [];
    public T[] Flat { get; set; } = [];
    public T[][] Nested { get; set; } = [];
}

public static class SerializerContextExample
{
    public static List<Dictionary<string, int>> SerializeAndCopy()
    {
        #region serializer_context_usage
        using var services = new ServiceCollection()
            .AddSerializerContext(new ApplicationSerializerContext())
            .BuildServiceProvider();

        var serializer = services.GetRequiredService<Serializer>();
        var copier = services.GetRequiredService<DeepCopier>();
        var input = new List<Dictionary<string, int>>
        {
            new() { ["count"] = 3 },
            new() { ["count"] = 5 }
        };

        var bytes = serializer.SerializeToArray(input);
        var restored = serializer.Deserialize<List<Dictionary<string, int>>>(bytes)!;
        var copy = copier.Copy(restored);
        copy[0]["count"] = 7;
        #endregion

        if (restored[0]["count"] != 3)
        {
            throw new InvalidOperationException("The copied dictionary must have independent mutable state.");
        }

        return copy;
    }
}
