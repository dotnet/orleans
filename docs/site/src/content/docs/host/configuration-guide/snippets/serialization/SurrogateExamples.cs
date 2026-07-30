using Orleans;

namespace Orleans.Docs.Snippets.Serialization;

// <surrogate_value_type>
// This is the foreign type, which you do not have control over.
public struct MyForeignLibraryValueType
{
    public MyForeignLibraryValueType(int num, string str, DateTimeOffset dto)
    {
        Num = num;
        String = str;
        DateTimeOffset = dto;
    }

    public int Num { get; }
    public string String { get; }
    public DateTimeOffset DateTimeOffset { get; }
}

// This is the surrogate which will act as a stand-in for the foreign type.
// Surrogates should use plain fields instead of properties for better performance.
[GenerateSerializer]
public struct MyForeignLibraryValueTypeSurrogate
{
    [Id(0)]
    public int Num;

    [Id(1)]
    public string String;

    [Id(2)]
    public DateTimeOffset DateTimeOffset;
}

// This is a converter that converts between the surrogate and the foreign type.
[RegisterConverter]
public sealed class MyForeignLibraryValueTypeSurrogateConverter :
    IConverter<MyForeignLibraryValueType, MyForeignLibraryValueTypeSurrogate>
{
    public MyForeignLibraryValueType ConvertFromSurrogate(
        in MyForeignLibraryValueTypeSurrogate surrogate) =>
        new(surrogate.Num, surrogate.String, surrogate.DateTimeOffset);

    public MyForeignLibraryValueTypeSurrogate ConvertToSurrogate(
        in MyForeignLibraryValueType value) =>
        new()
        {
            Num = value.Num,
            String = value.String,
            DateTimeOffset = value.DateTimeOffset
        };
}
// </surrogate_value_type>

// <surrogate_class_with_populator>
// The foreign type is not sealed, allowing other types to inherit from it.
public class MyForeignLibraryType
{
    public MyForeignLibraryType() { }

    public MyForeignLibraryType(int num, string str, DateTimeOffset dto)
    {
        Num = num;
        String = str;
        DateTimeOffset = dto;
    }

    public int Num { get; set; }
    public string String { get; set; }
    public DateTimeOffset DateTimeOffset { get; set; }
}

// The surrogate is defined as it was in the previous example.
[GenerateSerializer]
public struct MyForeignLibraryTypeSurrogate
{
    [Id(0)]
    public int Num;

    [Id(1)]
    public string String;

    [Id(2)]
    public DateTimeOffset DateTimeOffset;
}

// Implement the IConverter and IPopulator interfaces on the converter.
[RegisterConverter]
public sealed class MyForeignLibraryTypeSurrogateConverter :
    IConverter<MyForeignLibraryType, MyForeignLibraryTypeSurrogate>,
    IPopulator<MyForeignLibraryType, MyForeignLibraryTypeSurrogate>
{
    public MyForeignLibraryType ConvertFromSurrogate(
        in MyForeignLibraryTypeSurrogate surrogate) =>
        new(surrogate.Num, surrogate.String, surrogate.DateTimeOffset);

    public MyForeignLibraryTypeSurrogate ConvertToSurrogate(
        in MyForeignLibraryType value) =>
        new()
    {
        Num = value.Num,
        String = value.String,
        DateTimeOffset = value.DateTimeOffset
    };

    public void Populate(
        in MyForeignLibraryTypeSurrogate surrogate, MyForeignLibraryType value)
    {
        value.Num = surrogate.Num;
        value.String = surrogate.String;
        value.DateTimeOffset = surrogate.DateTimeOffset;
    }
}

// Application types can inherit from the foreign type, assuming they're not sealed
// since Orleans knows how to serialize it.
[GenerateSerializer]
public sealed class DerivedFromMyForeignLibraryType : MyForeignLibraryType
{
    public DerivedFromMyForeignLibraryType() { }

    public DerivedFromMyForeignLibraryType(
        int intValue, int num, string str, DateTimeOffset dto) : base(num, str, dto)
    {
        IntValue = intValue;
    }

    [Id(0)]
    public int IntValue { get; set; }
}
// </surrogate_class_with_populator>
