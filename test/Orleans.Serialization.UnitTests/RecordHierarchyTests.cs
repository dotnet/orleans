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

    // TODO: This type should cause a build error because "Bar" is an init-only non-auto property but has an [Id(...)] attribute.
    // It is suited for an diagnostic analyzer test, but the current implementation
    // of the source generator does not support execution as an analyzer.
    /*
    [GenerateSerializer]
    public record RecordWithInitOnlyManualProperty
    {
        private string _bar;

        [Id(0)]
        public string Bar
        {
            get => _bar;
            init
            {
                _bar = value;
                OnSetBar(_bar);
            }
        }

        public RecordWithInitOnlyManualProperty(string bar)
        {
            _bar = bar;
            OnSetBar(_bar);
        }

        private void OnSetBar(string bar)
        {
            // Ignore
            _ = bar;
        }
    }
    */
}