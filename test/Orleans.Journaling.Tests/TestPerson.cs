namespace Orleans.Journaling.Tests;

/// <summary>
/// Test class used for complex object serialization testing
/// </summary>
[GenerateSerializer]
public record class TestPerson
{
    [Id(0)]
    public int Id { get; set; }
    [Id(1)]
    public string? Name { get; set; }
    [Id(2)]
    public int Age { get; set; }
}
