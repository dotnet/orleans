using System;
using System.Collections.Generic;
using System.Collections.Frozen;
using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Codecs;
using Xunit;

namespace Orleans.Serialization.UnitTests;

[Trait("Category", "BVT")]
[Trait("Suite", "BVT")]
[Trait("Provider", "None")]
[Trait("Area", "Serialization")]
public sealed class CopierConstructorReferenceTests
{
    [Theory]
    [InlineData("nullable")]
    [InlineData("tuple")]
    [InlineData("value tuple")]
    [InlineData("pair")]
    [InlineData("immutable array")]
    [InlineData("immutable list")]
    [InlineData("immutable queue")]
    [InlineData("immutable stack")]
    [InlineData("immutable set")]
    [InlineData("immutable sorted set")]
    [InlineData("immutable dictionary")]
    [InlineData("immutable sorted dictionary")]
    [InlineData("frozen set")]
    [InlineData("frozen dictionary")]
    public void ConstructorsRetainReferencesWithoutQueryingIncompleteCopiers(string kind)
    {
        var dependency = new TraitProbe();
        var (copier, _) = Create(kind, dependency);
        Assert.Equal(0, dependency.Queries);
        dependency.Ready = true;
        Assert.False(copier.IsShallowCopyable());
        Assert.True(dependency.Queries > 0);
    }

    [Theory]
    [InlineData("nullable")]
    [InlineData("tuple")]
    [InlineData("value tuple")]
    [InlineData("pair")]
    [InlineData("immutable array")]
    [InlineData("immutable list")]
    [InlineData("immutable queue")]
    [InlineData("immutable stack")]
    [InlineData("immutable set")]
    [InlineData("immutable sorted set")]
    [InlineData("immutable dictionary")]
    [InlineData("immutable sorted dictionary")]
    [InlineData("frozen set")]
    [InlineData("frozen dictionary")]
    public void CompletedShallowDependenciesPreserveCopyIdentity(string kind)
    {
        var dependency = new TraitProbe { Shallow = true };
        var (copier, input) = Create(kind, dependency);
        Assert.Equal(0, dependency.Queries);
        dependency.Ready = true;
        Assert.True(copier.IsShallowCopyable());
        using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        using var context = services.GetRequiredService<CopyContextPool>().GetContext();
        Assert.Same(input, copier.DeepCopy(input, context));
        Assert.Equal(0, dependency.Copies);
    }

    private static (IOptionalDeepCopier Copier, object Input) Create(string kind, TraitProbe dependency)
    {
        var copier = new ProbeCopier<int>(dependency);
        var shallow = new ShallowCopier<int>();
        return kind switch
        {
            "nullable" => (new NullableCopier<int>(copier), (int?)42),
            "tuple" => (new TupleCopier<int, int>(copier, shallow), Tuple.Create(42, 17)),
            "value tuple" => (new ValueTupleCopier<int, int>(copier, shallow), (42, 17)),
            "pair" => (new KeyValuePairCopier<int, int>(copier, shallow), new KeyValuePair<int, int>(42, 17)),
            "immutable array" => (new ImmutableArrayCopier<int>(copier), ImmutableArray.Create(42)),
            "immutable list" => (new ImmutableListCopier<int>(copier), ImmutableList.Create(42)),
            "immutable queue" => (new ImmutableQueueCopier<int>(copier), ImmutableQueue.Create(42)),
            "immutable stack" => (new ImmutableStackCopier<int>(copier), ImmutableStack.Create(42)),
            "immutable set" => (new ImmutableHashSetCopier<int>(copier), ImmutableHashSet.Create(42)),
            "immutable sorted set" => (new ImmutableSortedSetCopier<int>(copier), ImmutableSortedSet.Create(42)),
            "immutable dictionary" => (new ImmutableDictionaryCopier<int, int>(copier, shallow), ImmutableDictionary<int, int>.Empty.Add(42, 17)),
            "immutable sorted dictionary" => (new ImmutableSortedDictionaryCopier<int, int>(copier, shallow), ImmutableSortedDictionary<int, int>.Empty.Add(42, 17)),
            "frozen set" => (new FrozenSetCopier<int>(copier), new[] { 42 }.ToFrozenSet()),
            "frozen dictionary" => (new FrozenDictionaryCopier<int, int>(copier, shallow), new Dictionary<int, int> { [42] = 17 }.ToFrozenDictionary()),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };
    }

    private sealed class TraitProbe
    {
        public bool Ready;
        public bool Shallow;
        public int Queries;
        public int Copies;
    }

    private sealed class ProbeCopier<T>(TraitProbe state) : IDeepCopier<T>, IOptionalDeepCopier
    {
        public bool IsShallowCopyable()
        {
            if (!state.Ready) throw new InvalidOperationException("A consuming constructor queried an incomplete copier.");
            state.Queries++;
            return state.Shallow;
        }

        public T? DeepCopy(T? input, CopyContext context)
        {
            state.Copies++;
            throw new InvalidOperationException("A shallow dependency must not be invoked.");
        }
    }
}
