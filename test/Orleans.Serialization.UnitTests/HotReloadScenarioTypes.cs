using Orleans;

namespace HotReloadScenario;

[GenerateSerializer]
public sealed class HotReloadAddedType
{
    [Id(0)] public int Value { get; set; }
}

[GenerateSerializer]
public sealed class HotReloadHolderType
{
    [Id(0)] public HotReloadAddedType? Inner { get; set; }
}
