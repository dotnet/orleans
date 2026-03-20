using System;
using Orleans;
using Microsoft.Extensions.Options;
using Orleans.BroadcastChannel;
using Orleans.Configuration;
using Orleans.Serialization.TypeSystem;
using Xunit;

namespace UnitTests;

/// <summary>
/// Tests for <see cref="ConstructorChannelNamespacePredicateProvider"/> predicate type registration and resolution.
/// </summary>
[TestCategory("BVT"), TestCategory("Predicates")]
public class ConstructorChannelNamespacePredicateProviderTests
{
    private static ConstructorChannelNamespacePredicateProvider CreateProvider(params Type[] grainClasses)
    {
        var options = new GrainTypeOptions();
        foreach (var grainClass in grainClasses)
        {
            options.Classes.Add(grainClass);
        }

        return new ConstructorChannelNamespacePredicateProvider(Options.Create(options));
    }

    [Fact]
    public void RegisteredPredicateType_Succeeds()
    {
        var provider = CreateProvider(typeof(TestGrainWithChannelPredicate));
        var pattern = ConstructorChannelNamespacePredicateProvider.FormatPattern(typeof(TestChannelPredicate), constructorArgument: null);

        var result = provider.TryGetPredicate(pattern, out var predicate);

        Assert.True(result);
        Assert.NotNull(predicate);
        Assert.IsType<TestChannelPredicate>(predicate);
    }

    [Fact]
    public void RegisteredPredicateTypeWithArg_Succeeds()
    {
        var provider = CreateProvider();
        provider.RegisterPredicateType(typeof(TestChannelPredicateWithArg));
        var pattern = ConstructorChannelNamespacePredicateProvider.FormatPattern(typeof(TestChannelPredicateWithArg), constructorArgument: "ch-ns");

        var result = provider.TryGetPredicate(pattern, out var predicate);

        Assert.True(result);
        Assert.NotNull(predicate);
        Assert.IsType<TestChannelPredicateWithArg>(predicate);
        Assert.True(predicate.IsMatch("ch-ns"));
    }

    [Fact]
    public void UnregisteredType_Throws()
    {
        var provider = CreateProvider();
        var pattern = $"ctor:{RuntimeTypeNameFormatter.Format(typeof(System.IO.FileInfo))}:C:\\temp\\evil.txt";

        Assert.Throws<InvalidOperationException>(() => provider.TryGetPredicate(pattern, out _));
    }

    [Fact]
    public void UnregisteredArbitraryType_Throws()
    {
        var provider = CreateProvider();
        var pattern = $"ctor:{RuntimeTypeNameFormatter.Format(typeof(System.Collections.ArrayList))}";

        Assert.Throws<InvalidOperationException>(() => provider.TryGetPredicate(pattern, out _));
    }

    [Fact]
    public void NonMatchingPrefix_ReturnsFalse()
    {
        var provider = CreateProvider();

        var result = provider.TryGetPredicate("namespace:test", out var predicate);

        Assert.False(result);
        Assert.Null(predicate);
    }

    [ImplicitChannelSubscription(typeof(TestChannelPredicate))]
    private class TestGrainWithChannelPredicate { }

    

    public class TestChannelPredicate : IChannelNamespacePredicate
    {
        public string PredicatePattern => ConstructorChannelNamespacePredicateProvider.FormatPattern(typeof(TestChannelPredicate), constructorArgument: null);

        public bool IsMatch(string streamNamespace) => true;
    }

    public class TestChannelPredicateWithArg : IChannelNamespacePredicate
    {
        private readonly string _namespace;

        public TestChannelPredicateWithArg(string ns) => _namespace = ns;

        public string PredicatePattern => ConstructorChannelNamespacePredicateProvider.FormatPattern(typeof(TestChannelPredicateWithArg), _namespace);

        public bool IsMatch(string streamNamespace) => string.Equals(_namespace, streamNamespace, StringComparison.Ordinal);
    }
}
