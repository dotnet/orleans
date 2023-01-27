#nullable enable
using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Orleans.Serialization.UnitTests;

public class RecordSerializationTests
{
    private readonly ServiceProvider _services;
    private readonly Serializer _serializer;

    public RecordSerializationTests()
    {
        _services = new ServiceCollection()
            .AddSerializer()
            .BuildServiceProvider();
        _serializer = _services.GetRequiredService<Serializer>();
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

    [Fact]
    public void CanSerializeRecordsInList()
    {
        var serializer = _services.GetRequiredService<Serializer>();
        var expected = new List<DerivedFromNonEmptyAbstractRecord>
        {
            new DerivedFromNonEmptyAbstractRecord("foo") { Bar = "bar" },
            new DerivedFromNonEmptyAbstractRecord("foo2") { Bar = "bar2" },
        };
        var bytes = serializer.SerializeToArray(expected);
        var result = serializer.Deserialize<List<DerivedFromNonEmptyAbstractRecord>>(bytes);
        Assert.Equal(expected.Count, result.Count);
        Assert.Equal(expected[0], result[0]);
        Assert.Equal(expected[1], result[1]);
    }

        [Fact]
        public void Can_Roundtrip_WithListOfObject_Fruit()
        {
            var original = new FooWithListOfObject
            {
                Items = new List<object> { new FruitRecord("Banana"), new FruitRecord("Mango") },
                Bar = new FooRecord(Guid.NewGuid())
            };

            var bytes = _serializer.SerializeToArray(original);

            var deserialized = _serializer.Deserialize<FooWithListOfObject>(bytes);

            Assert.NotNull(deserialized.Bar);
            Assert.Equal(original.Bar, deserialized.Bar);
        }

        [Fact]
        public void Can_Roundtrip_With_Them_Apples()
        {
            var original = new TwoObjects
            {
                One = new AppleRecord("Golden Delicious"),
                Two = new AppleRecord("Granny Smith")
            };

            var bytes = _serializer.SerializeToArray(original);

            var deserialized = _serializer.Deserialize<TwoObjects>(bytes);

            Assert.NotNull(deserialized.One);
            Assert.NotNull(deserialized.Two);
            Assert.Equal(original.One, deserialized.One);
            Assert.Equal(original.Two, deserialized.Two);
        }

        [Fact]
        public void Can_Roundtrip_WithListOfObject_Apple()
        {
            var original = new FooWithListOfObject
            {
                Items = new List<object> { new AppleRecord("Golden Delicious"), new AppleRecord("Granny Smith") },
                Bar = new FooRecord(Guid.NewGuid())
            };

            var bytes = _serializer.SerializeToArray(original);

            var deserialized = _serializer.Deserialize<FooWithListOfObject>(bytes);

            Assert.NotNull(deserialized.Items);
            Assert.Equal(original.Items.Count, deserialized.Items.Count);
            Assert.Equal(original.Items[0], deserialized.Items[0]);
            Assert.Equal(original.Items[1], deserialized.Items[1]);

            Assert.NotNull(deserialized.Bar);
            Assert.Equal(original.Bar, deserialized.Bar);
        }

        [Fact]
        public void Can_Roundtrip_WithListOfFruit_Fruit()
        {
            var original = new FooWithListOfFruit()
            {
                Items = new List<FruitRecord> { new FruitRecord("Banana"), new FruitRecord("Mango") },
                Bar = new FooRecord(Guid.NewGuid())
            };

            var bytes = _serializer.SerializeToArray(original);

            var deserialized = _serializer.Deserialize<FooWithListOfFruit>(bytes);

            Assert.NotNull(deserialized.Bar);
            Assert.Equal(original.Bar, deserialized.Bar);
        }

        [Fact]
        public void Can_Roundtrip_WithListOfFruit_Apple()
        {
            var original = new FooWithListOfFruit()
            {
                Items = new List<FruitRecord> { new AppleRecord("Golden Delicious"), new AppleRecord("Granny Smith") },
                Bar = new FooRecord(Guid.NewGuid())
            };

            var bytes = _serializer.SerializeToArray(original);

            var deserialized = _serializer.Deserialize<FooWithListOfFruit>(bytes);

            Assert.NotNull(deserialized.Bar);
            Assert.Equal(original.Bar, deserialized.Bar);
        }

        [Fact]
        public void Can_Roundtrip_WithListOfApple_Apple()
        {
            var original = new FooWithListOfApple()
            {
                Items = new List<AppleRecord> { new AppleRecord("Golden Delicious"), new AppleRecord("Granny Smith") },
                Bar = new FooRecord(Guid.NewGuid())
            };

            var bytes = _serializer.SerializeToArray(original);

            var deserialized = _serializer.Deserialize<FooWithListOfApple>(bytes);

            Assert.NotNull(deserialized.Bar);
            Assert.Equal(original.Bar, deserialized.Bar);
        }

        [Fact]
        public void Can_Roundtrip_Fruit_Apple()
        {
            FruitRecord original = new AppleRecord("Golden Delicious");

            var bytes = _serializer.SerializeToArray(original);

            var deserialized = _serializer.Deserialize<FruitRecord>(bytes);

            Assert.NotNull(deserialized);
            Assert.Equal(original, deserialized);
        }

        [Fact]
        public void Can_Roundtrip_Foo()
        {
            var original = new FooRecord(Guid.NewGuid());

            var bytes = _serializer.SerializeToArray(original);

            var deserialized = _serializer.Deserialize<FooRecord>(bytes);

            Assert.NotNull(deserialized);
            Assert.Equal(original, deserialized);
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

[GenerateSerializer]
public class FooWithListOfObject
{
    [Id(1)]
    public List<object>? Items { get; set; }

    [Id(2)]
    public FooRecord? Bar { get; set; }
}

[GenerateSerializer]
public class TwoObjects
{
    [Id(0)]
    public object? One { get; set; }

    [Id(1)]
    public object? Two { get; set; }
}

[GenerateSerializer]
public class FooWithListOfFruit
{
    [Id(1)]
    public List<FruitRecord>? Items { get; set; }

    [Id(2)]
    public FooRecord? Bar { get; set; }
}

[GenerateSerializer]
public class FooWithListOfApple
{
    [Id(1)]
    public List<AppleRecord>? Items { get; set; }

    [Id(2)]
    public FooRecord? Bar { get; set; }
}

[GenerateSerializer]
public record FruitRecord(string Name);

[GenerateSerializer]
public record AppleRecord(string Name) : FruitRecord(Name);

[GenerateSerializer]
public record FooRecord([property: Id(0)] Guid Id);