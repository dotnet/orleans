using Orleans;

namespace UnitTests.SerializerExternalModels;

[GenerateSerializer]
public record struct Person2External(int Age, string Name)
{
    [Id(0)]
    public string FavouriteColor { get; set; }

    [Id(1)]
    public string StarSign { get; set; }
}
