using System;
using System.Collections.Generic;
using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Codecs;
using Xunit;

namespace Orleans.Serialization.UnitTests;

[Trait("Category", "BVT")]
[Trait("Suite", "BVT")]
[Trait("Provider", "None")]
[Trait("Area", "Serialization")]
public sealed class CopierConstructorReferenceTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void TypedValueTupleCopiesUseCompletedShallowResult(int arity)
    {
        var dependency = new TraitProbe { Shallow = true };
        Type[] copierDefinitions =
        [
            typeof(ValueTupleCopier<>), typeof(ValueTupleCopier<,>), typeof(ValueTupleCopier<,,>), typeof(ValueTupleCopier<,,,>),
            typeof(ValueTupleCopier<,,,,>), typeof(ValueTupleCopier<,,,,,>), typeof(ValueTupleCopier<,,,,,,>), typeof(ValueTupleCopier<,,,,,,,>)
        ];
        Type[] valueDefinitions =
        [
            typeof(ValueTuple<>), typeof(ValueTuple<,>), typeof(ValueTuple<,,>), typeof(ValueTuple<,,,>),
            typeof(ValueTuple<,,,,>), typeof(ValueTuple<,,,,,>), typeof(ValueTuple<,,,,,,>), typeof(ValueTuple<,,,,,,,>)
        ];
        var arguments = new Type[arity];
        var copiers = new object[arity];
        var values = new object[arity];
        var elementCopier = new ProbeCopier<int>(dependency);
        for (var index = 0; index < arity; index++)
        {
            arguments[index] = typeof(int);
            copiers[index] = elementCopier;
            values[index] = index;
        }
        if (arity == 8)
        {
            arguments[7] = typeof(ValueTuple<int>);
            copiers[7] = new ProbeCopier<ValueTuple<int>>(dependency);
            values[7] = ValueTuple.Create(7);
        }
        var copierType = copierDefinitions[arity - 1].MakeGenericType(arguments);
        var valueType = valueDefinitions[arity - 1].MakeGenericType(arguments);
        var copier = Activator.CreateInstance(copierType, copiers)!;
        var input = Activator.CreateInstance(valueType, values)!;
        var copy = copierType.GetMethod(nameof(IDeepCopier<int>.DeepCopy), new[] { valueType, typeof(CopyContext) })!;
        Assert.Equal(0, dependency.Queries);
        dependency.Ready = true;
        using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        using var context = services.GetRequiredService<CopyContextPool>().GetContext();
        Assert.Equal(input, copy.Invoke(copier, new[] { input, context }));
        Assert.Equal(arity, dependency.Queries);
        Assert.Equal(input, copy.Invoke(copier, new[] { input, context }));
        Assert.Equal(arity, dependency.Queries);
        Assert.Equal(0, dependency.Copies);
    }

    [Fact]
    public void TypedValueTupleCopiesRetainShallowMembersAndIsolateMutableMembers()
    {
        var dependency = new TraitProbe { Shallow = true };
        var copier = new ValueTupleCopier<string, List<int>>(
            new ProbeCopier<string>(dependency), new ListCopier<int>(new ShallowCopier<int>()));
        Assert.Equal(0, dependency.Queries);
        dependency.Ready = true;
        using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        using var context = services.GetRequiredService<CopyContextPool>().GetContext();
        var input = (Item1: "shallow", Item2: new List<int> { 13, 17 });
        var result = copier.DeepCopy(input, context);
        Assert.Same(input.Item1, result.Item1);
        Assert.NotSame(input.Item2, result.Item2);
        Assert.Equal(input.Item2, result.Item2);
        result.Item2[0] = 23;
        Assert.Equal(13, input.Item2[0]);
        Assert.Equal(0, dependency.Copies);
    }

    [Fact]
    public void SharedTupleDependenciesBoundFirstAndRepeatedCopyQueries()
    {
        const int levels = 14;
        var (copier, input, dependency) = CreateSharedTupleChain(levels);
        Assert.Equal(0, dependency.Queries);
        dependency.Ready = true;
        using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        using var context = services.GetRequiredService<CopyContextPool>().GetContext();

        var first = copier.DeepCopy(input, context);
        var firstQueries = dependency.Queries;
        dependency.Queries = 0;
        for (var iteration = 0; iteration < 100; iteration++)
            Assert.Same(input, copier.DeepCopy(input, context));
        var repeatedQueries = dependency.Queries;

        output.WriteLine($"Tuple/leaf implementations: {levels + 1}; query-counting decorators: {levels}; dependency edges: {levels * 2}; first-copy dependency trait queries: {firstQueries}; 100 repeated-copy dependency trait queries: {repeatedQueries}.");
        Assert.Same(input, first);
        Assert.Equal(levels * 2, firstQueries);
        Assert.Equal(0, repeatedQueries);
        Assert.Equal(0, dependency.Copies);
    }

    [Fact]
    public void SharedTupleShallowQueriesVisitDependencyEdgesOnce()
    {
        const int levels = 14;
        var (copier, _, dependency) = CreateSharedTupleChain(levels);
        Assert.Equal(0, dependency.Queries);
        dependency.Ready = true;

        Assert.True(copier.IsShallowCopyable());
        var firstQueries = dependency.Queries;
        dependency.Queries = 0;
        Assert.True(copier.IsShallowCopyable());
        var repeatedQueries = dependency.Queries;

        output.WriteLine($"Tuple/leaf implementations: {levels + 1}; query-counting decorators: {levels}; first trait-query calls: {firstQueries}; repeated trait-query calls: {repeatedQueries}.");
        Assert.Equal(levels * 2 + 1, firstQueries);
        Assert.Equal(1, repeatedQueries);
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
    public void ConstructorsRetainReferencesWithoutQueryingIncompleteCopiers(string kind)
    {
        var dependency = new TraitProbe();
        var (copier, _) = Create(kind, dependency);
        Assert.Equal(0, dependency.Queries);
        dependency.Ready = true;
        Assert.False(copier.IsShallowCopyable());
        Assert.Equal(1, dependency.Queries);
        Parallel.For(0, 64, _ => Assert.False(copier.IsShallowCopyable()));
        Assert.Equal(1, dependency.Queries);
        Assert.False(copier.IsShallowCopyable());
        Assert.Equal(1, dependency.Queries);
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
        Assert.Equal(1, dependency.Queries);
        using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        using var context = services.GetRequiredService<CopyContextPool>().GetContext();
        Assert.Same(input, copier.DeepCopy(input, context));
        Assert.Same(input, copier.DeepCopy(input, context));
        Assert.Equal(1, dependency.Queries);
        Parallel.For(0, 64, _ => Assert.True(copier.IsShallowCopyable()));
        Assert.Equal(1, dependency.Queries);
        Assert.Equal(0, dependency.Copies);
    }

    private static (IOptionalDeepCopier Copier, object Input, TraitProbe Dependency) CreateSharedTupleChain(int levels)
    {
        var dependency = new TraitProbe { Shallow = true };
        IOptionalDeepCopier copier = new ProbeCopier<int>(dependency);
        object input = 42;
        var factory = typeof(CopierConstructorReferenceTests).GetMethod(nameof(CreateSharedTupleLevel),
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        for (var level = 0; level < levels; level++)
        {
            (copier, input) = ((IOptionalDeepCopier, object))factory.MakeGenericMethod(input.GetType())
                .Invoke(null, new object[] { copier, input, dependency })!;
        }

        return (copier, input, dependency);
    }

    private static (IOptionalDeepCopier Copier, object Input) CreateSharedTupleLevel<T>(
        IOptionalDeepCopier child, object input, TraitProbe dependency)
    {
        var typed = Assert.IsAssignableFrom<IDeepCopier<T>>(child);
        var copier = new TupleCopier<T, T>(typed, typed);
        return (new CountingCopier<Tuple<T, T>>(copier, dependency), Tuple.Create((T)input, (T)input));
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

    private sealed class CountingCopier<T>(IDeepCopier<T> copier, TraitProbe state) : IDeepCopier<T>, IOptionalDeepCopier
    {
        public bool IsShallowCopyable()
        {
            state.Queries++;
            return ((IOptionalDeepCopier)copier).IsShallowCopyable();
        }

        public T? DeepCopy(T? input, CopyContext context) => copier.DeepCopy(input, context);
    }
}
