using Orleans;

namespace UnitTests.SerializerExternalModels;

[GenerateSerializer]
public record struct Person2ExternalStruct(int Age, string Name)
{
    [Id(0)]
    public string FavouriteColor { get; set; }

    [Id(1)]
    public string StarSign { get; set; }
}

#if NET6_0_OR_GREATER
[GenerateSerializer]
public record Person2External(int Age, string Name)
{
    [Id(0)]
    public string FavouriteColor { get; set; }

    [Id(1)]
    public string StarSign { get; set; }
}
#endif
