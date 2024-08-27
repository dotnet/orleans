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

[GenerateSerializer]
public record struct GenericPersonExternalStruct<T>(T CtorParam, string Name)
{
    [Id(0)]
    public T BodyParam { get; set; }

    [Id(1)]
    public string StarSign { get; set; }
}

[GenerateSerializer]
public readonly record struct ReadonlyGenericPersonExternalStruct<T>(T CtorParam, string Name)
{
    [Id(0)]
    public T BodyParam { get; init; }

    [Id(1)]
    public string StarSign { get; init; }
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

[GenerateSerializer]
public record GenericPersonExternal<T>(T CtorParam, string Name)
{
    [Id(0)]
    public T BodyParam { get; set; }

    [Id(1)]
    public string StarSign { get; set; }
}
#endif
