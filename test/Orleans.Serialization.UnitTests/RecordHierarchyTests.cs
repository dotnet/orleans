using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Orleans.Serialization.UnitTests;

public class RecordSerializationTests
{
    private readonly ServiceProvider _services;

    [GenerateSerializer]
    public abstract record EmptyAbstractRecord
    {
    }

    [GenerateSerializer]
    public record DerivedFromEmptyAbstractRecord : EmptyAbstractRecord
    {
        [Id(0)]
        public string Foo { get; init; }

        public DerivedFromEmptyAbstractRecord(string foo)
        {
            Foo = foo;
        }
    }

    [GenerateSerializer]
    public abstract record NonEmptyAbstractRecord
    {
        [Id(0)]
        public string Bar { get; init; }

        protected NonEmptyAbstractRecord()
        {
            Bar = "bar";
        }
    }

    [GenerateSerializer]
    public record DerivedFromNonEmptyAbstractRecord : NonEmptyAbstractRecord
    {
        [Id(0)]
        public string Foo { get; init; }

        public DerivedFromNonEmptyAbstractRecord(string foo) : base()
        {
            Foo = foo;
        }
    }

    [GenerateSerializer]
    public record RecordHierarchyDerived([property: Id(1)] string Message) : RecordHierarchyMiddle
    {
        public override string Type => "Foo";
    }

    [GenerateSerializer]
    public abstract record RecordHierarchyMiddle : RecordHierarchyBase;

    [GenerateSerializer]
    public abstract record RecordHierarchyBase
    {
        public abstract string Type { get; }
    }

    public RecordSerializationTests()
    {
        _services = new ServiceCollection()
            .AddSerializer()
            .BuildServiceProvider();
    }

    [Fact]
    public void CanSerializeEmptyAbstractRecord()
    {
        var serializer = _services.GetRequiredService<Serializer<DerivedFromEmptyAbstractRecord>>();

        var key = new DerivedFromEmptyAbstractRecord("Sample Foo");
        var bytes = serializer.SerializeToArray(key);
        var newKey = serializer.Deserialize(bytes);

        Assert.Equal(key, newKey);
    }

    [Fact]
    public void CanSerializePopulatedAbstractRecord()
    {
        var serializer = _services.GetRequiredService<Serializer<DerivedFromNonEmptyAbstractRecord>>();

        var key = new DerivedFromNonEmptyAbstractRecord("Sample Foo");
        var bytes = serializer.SerializeToArray(key);
        var newKey = serializer.Deserialize(bytes);

        Assert.Equal(key, newKey);
    }

    [Fact]
    public void CanSerializeRecordsWithEmptyHierarchyLayers()
    {
        var serializer = _services.GetRequiredService<Serializer<RecordHierarchyDerived>>();

        var expected = new RecordHierarchyDerived(Message: "test");
        var bytes = serializer.SerializeToArray(expected);
        var result = serializer.Deserialize(bytes);

        Assert.Equal(expected, result);
    }
}