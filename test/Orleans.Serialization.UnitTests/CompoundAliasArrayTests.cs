using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization.TypeSystem;
using Xunit;

namespace Orleans.Serialization.UnitTests;

[Trait("Category", "BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Serialization")]
public sealed class CompoundAliasArrayTests : IDisposable
{
    private readonly ServiceProvider _services = new ServiceCollection()
        .AddSerializer(builder => builder.Configure(options => options.AddAllowedType(typeof(MyTypeAliasClass))))
        .BuildServiceProvider();

    [Theory]
    [InlineData("(\"marker\")[]", "Resolved.Marker[],New.Assembly")]
    [InlineData("(\"marker\")[,]", "Resolved.Marker[,],New.Assembly")]
    [InlineData("(\"marker\")[,,][]", "Resolved.Marker[,,][],New.Assembly")]
    [InlineData("(\"marker\")[][]", "Resolved.Marker[][],New.Assembly")]
    [InlineData("(\"marker\")[],Old.Assembly", "Resolved.Marker[],New.Assembly")]
    [InlineData("Container`1[[(\"marker\")[]]],Container.Assembly", "Container`1[[Resolved.Marker[],New.Assembly]],Container.Assembly")]
    public void Rewriter_QualifiesTheArrayInsteadOfItsElement(string input, string expected)
    {
        var state = 0;
        var result = RuntimeTypeNameRewriter.Rewrite(
            RuntimeTypeNameParser.Parse(input),
            static (in QualifiedType type, ref int state) => type,
            static (TupleTypeSpec alias, ref int state) => RuntimeTypeNameParser.Parse("Resolved.Marker,New.Assembly"),
            ref state);

        Assert.Equal(expected, result.Format());
        Assert.Equal(expected, RuntimeTypeNameParser.Parse(result.Format()).Format());
    }

    [Fact]
    public void Rewriter_PreservesUnchangedArrayNodes()
    {
        var original = RuntimeTypeNameParser.Parse("Unchanged.Element[,],Original.Assembly");
        var state = 0;

        var result = RuntimeTypeNameRewriter.Rewrite(
            original,
            static (in QualifiedType type, ref int state) => type,
            ref state);

        Assert.Same(original, result);
    }

    [Theory]
    [InlineData(typeof(CompoundAliasArrayElement[]))]
    [InlineData(typeof(CompoundAliasArrayElement[][]))]
    [InlineData(typeof(CompoundAliasArrayElement[,]))]
    [InlineData(typeof(CompoundAliasArrayElement[,,]))]
    [InlineData(typeof(CompoundAliasArrayEnvelope<CompoundAliasArrayElement[]>))]
    [InlineData(typeof(CompoundAliasArrayEnvelope<CompoundAliasArrayElement[][]>))]
    [InlineData(typeof(List<CompoundAliasArrayElement[]>))]
    [InlineData(typeof(List<CompoundAliasArrayElement>[]))]
    public void TypeConverter_RoundTripsCompoundAliasedArrayTypes(Type type)
    {
        var converter = _services.GetRequiredService<TypeConverter>();

        var formatted = converter.Format(type);

        Assert.Contains("(\"array-element\",[_custom_type_alias_],\"v1\")", formatted);
        Assert.Equal(type, converter.Parse(formatted));
        Assert.True(converter.TryParse(formatted, out var parsed));
        Assert.Equal(type, parsed);
    }

    [Theory]
    [InlineData("TypedNull")]
    [InlineData("TypedEmpty")]
    [InlineData("TypedVector")]
    [InlineData("TypedJagged")]
    [InlineData("TypedRankTwo")]
    [InlineData("TypedRankThree")]
    [InlineData("TypedEnvelope")]
    [InlineData("UntypedElement")]
    [InlineData("UntypedGenericElementArray")]
    public void SupportedLegacyPayloads_PreserveBytesAndDeserialize(string name) => CheckPayload(name);

    [Theory]
    [InlineData("UntypedEmpty")]
    [InlineData("UntypedVector")]
    [InlineData("UntypedJagged")]
    [InlineData("UntypedRankTwo")]
    [InlineData("UntypedRankThree")]
    [InlineData("UntypedEmptyRankTwo")]
    [InlineData("UntypedEnvelope")]
    [InlineData("UntypedList")]
    [InlineData("UntypedCycle")]
    public void PreviouslyUnreadableLegacyPayloads_PreserveBytesAndDeserialize(string name) => CheckPayload(name);

    public void Dispose() => _services.Dispose();

    private void CheckPayload(string name)
    {
        var shared = new CompoundAliasArrayElement { Value = 42 };
        var vector = new CompoundAliasArrayElement[] { shared, null!, shared };
        var jagged = new CompoundAliasArrayElement[][] { vector, [], null!, vector };
        var rankTwo = new CompoundAliasArrayElement[,] { { shared, null! }, { shared, new() { Value = 84 } } };
        var rankThree = new CompoundAliasArrayElement[1, 1, 2] { { { shared, shared } } };
        var envelope = new CompoundAliasArrayEnvelope<CompoundAliasArrayElement[]> { Value = vector };

        switch (name)
        {
            case "TypedNull":
                Check<CompoundAliasArrayElement[]?>(name, null, Assert.Null);
                break;
            case "TypedEmpty":
                Check(name, Array.Empty<CompoundAliasArrayElement>(), Assert.Empty);
                break;
            case "TypedVector":
                Check(name, vector, AssertVector);
                break;
            case "TypedJagged":
                Check(name, jagged, AssertJagged);
                break;
            case "TypedRankTwo":
                Check(name, rankTwo, AssertRankTwo);
                break;
            case "TypedRankThree":
                Check(name, rankThree, AssertRankThree);
                break;
            case "TypedEnvelope":
                Check(name, envelope, result => AssertVector(result.Value));
                break;
            case "UntypedElement":
                Check<object>(name, shared, result => Assert.Equal(42, Assert.IsType<CompoundAliasArrayElement>(result).Value));
                break;
            case "UntypedGenericElementArray":
                Check<object>(name, new List<CompoundAliasArrayElement>[] { [shared] }, result =>
                {
                    var array = Assert.IsType<List<CompoundAliasArrayElement>[]>(result);
                    Assert.Equal(42, Assert.Single(Assert.Single(array)).Value);
                });
                break;
            case "UntypedEmpty":
                Check<object>(name, Array.Empty<CompoundAliasArrayElement>(), result => Assert.Empty(Assert.IsType<CompoundAliasArrayElement[]>(result)));
                break;
            case "UntypedVector":
                Check<object>(name, vector, result => AssertVector(Assert.IsType<CompoundAliasArrayElement[]>(result)));
                break;
            case "UntypedJagged":
                Check<object>(name, jagged, result => AssertJagged(Assert.IsType<CompoundAliasArrayElement[][]>(result)));
                break;
            case "UntypedRankTwo":
                Check<object>(name, rankTwo, result => AssertRankTwo(Assert.IsType<CompoundAliasArrayElement[,]>(result)));
                break;
            case "UntypedRankThree":
                Check<object>(name, rankThree, result => AssertRankThree(Assert.IsType<CompoundAliasArrayElement[,,]>(result)));
                break;
            case "UntypedEmptyRankTwo":
                Check<object>(name, new CompoundAliasArrayElement[0, 2], result =>
                {
                    var array = Assert.IsType<CompoundAliasArrayElement[,]>(result);
                    Assert.Equal(0, array.GetLength(0));
                    Assert.Equal(2, array.GetLength(1));
                    Assert.Empty(array);
                });
                break;
            case "UntypedEnvelope":
                Check<object>(name, envelope, result => AssertVector(Assert.IsType<CompoundAliasArrayEnvelope<CompoundAliasArrayElement[]>>(result).Value));
                break;
            case "UntypedList":
                Check<object>(name, new List<CompoundAliasArrayElement[]> { vector, vector }, result =>
                {
                    var list = Assert.IsType<List<CompoundAliasArrayElement[]>>(result);
                    Assert.Equal(2, list.Count);
                    AssertVector(list[0]);
                    Assert.Same(list[0], list[1]);
                });
                break;
            case "UntypedCycle":
                shared.Reference = vector;
                Check<object>(name, vector, result =>
                {
                    var array = Assert.IsType<CompoundAliasArrayElement[]>(result);
                    AssertVector(array);
                    Assert.Same(array, array[0].Reference);
                });
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown payload fixture.");
        }
    }

    private void Check<T>(string name, T original, Action<T> assert, [CallerFilePath] string sourceFile = "")
    {
        // Captured with unmodified dotnet/orleans main 9bb744ad5fe9bd0a512052f1b11c1d6b6f7a44f1
        // (rewriter blob 8a7e9d2ef03fbbe0cdda27e322eb2ad7cb6dc095), Orleans.Serialization 10.0.0.0.
        // Production SerializeToArray<T> emitted identical fixtures on .NET 8 and .NET 10.
        // The declarations below and this test assembly's name are part of the fixture type identities.
        var serializer = _services.GetRequiredService<Serializer>();
        var path = Path.Combine(Path.GetDirectoryName(sourceFile)!, "snapshots", $"{nameof(CompoundAliasArrayTests)}.{name}.verified.hex.txt");
        var baseline = Convert.FromHexString(File.ReadAllText(path).Trim());

        assert(serializer.Deserialize<T>(baseline)!);
        Assert.Equal(baseline, serializer.SerializeToArray(original));
    }

    private static void AssertVector(CompoundAliasArrayElement[] result)
    {
        Assert.Equal(3, result.Length);
        Assert.Equal(42, result[0].Value);
        Assert.Null(result[1]);
        Assert.Same(result[0], result[2]);
    }

    private static void AssertJagged(CompoundAliasArrayElement[][] result)
    {
        Assert.Equal(4, result.Length);
        AssertVector(result[0]);
        Assert.Empty(result[1]);
        Assert.Null(result[2]);
        Assert.Same(result[0], result[3]);
    }

    private static void AssertRankTwo(CompoundAliasArrayElement[,] result)
    {
        Assert.Equal(2, result.GetLength(0));
        Assert.Equal(2, result.GetLength(1));
        Assert.Equal(42, result[0, 0].Value);
        Assert.Null(result[0, 1]);
        Assert.Same(result[0, 0], result[1, 0]);
        Assert.Equal(84, result[1, 1].Value);
    }

    private static void AssertRankThree(CompoundAliasArrayElement[,,] result)
    {
        Assert.Equal(1, result.GetLength(0));
        Assert.Equal(1, result.GetLength(1));
        Assert.Equal(2, result.GetLength(2));
        Assert.Equal(42, result[0, 0, 0].Value);
        Assert.Same(result[0, 0, 0], result[0, 0, 1]);
    }
}

[GenerateSerializer]
[CompoundTypeAlias("array-element", typeof(MyTypeAliasClass), "v1")]
public sealed class CompoundAliasArrayElement
{
    [Id(0)]
    public int Value { get; set; }

    [Id(1)]
    public object? Reference { get; set; }
}

[GenerateSerializer]
[Alias("compound-alias-array-envelope`1")]
public sealed class CompoundAliasArrayEnvelope<T>
{
    [Id(0)]
    public T Value { get; set; } = default!;
}
