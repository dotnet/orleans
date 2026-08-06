using Orleans;

namespace Orleans.Docs.Snippets.Serialization;

// <basic_employee_class>
[GenerateSerializer]
public class Employee
{
    [Id(0)]
    public string Name { get; set; }
}
// </basic_employee_class>

// <inheritance_publication_book>
[GenerateSerializer]
public class Publication
{
    [Id(0)]
    public string Title { get; set; }
}

[GenerateSerializer]
public class Book : Publication
{
    [Id(0)]
    public string ISBN { get; set; }
}
// </inheritance_publication_book>

// <custom_struct_private_readonly>
[GenerateSerializer]
public struct MyCustomStruct
{
    public MyCustomStruct(int intProperty, int intField)
    {
        IntProperty = intProperty;
        _intField = intField;
    }

    [Id(0)]
    public int IntProperty { get; }

    [Id(1)] private readonly int _intField;
    public int GetIntField() => _intField;

    public override string ToString() => $"{nameof(_intField)}: {_intField}, {nameof(IntProperty)}: {IntProperty}";
}
// </custom_struct_private_readonly>

// <record_primary_constructor>
[GenerateSerializer]
public record MyRecord(string A, string B)
{
    // ID 0 won't clash with A in primary constructor as they don't share identities
    [Id(0)]
    public string C { get; init; }
}
// </record_primary_constructor>

// <best_practices_id_overlap>
[GenerateSerializer]
public class MyBaseClass
{
    [Id(0)]
    public int MyBaseInt { get; set; }
}

[GenerateSerializer]
public sealed class MySubClass : MyBaseClass
{
    [Id(0)]
    public int MySubInt { get; set; }
}
// </best_practices_id_overlap>
