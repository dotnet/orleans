using System;
using Orleans;
using Microsoft.Extensions.Options;
using Orleans.BroadcastChannel;
using Orleans.Configuration;
using Orleans.Serialization.TypeSystem;
using Xunit;
using System.Collections;

namespace UnitTests;

/// <summary>
/// Tests for <see cref="ConstructorChannelNamespacePredicateProvider"/> predicate type registration and resolution.
/// </summary>
[TestCategory("BVT"), TestCategory("Predicates")]
public class ConstructorChannelNamespacePredicateProviderTests
{
    private static ConstructorChannelNamespacePredicateProvider CreateProvider() => new();

    [Fact]
    public void RegisteredPredicateType_Succeeds()
    {
        var provider = CreateProvider();
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
        var pattern = ConstructorChannelNamespacePredicateProvider.FormatPattern(typeof(RegexChannelNamespacePredicate), constructorArgument: "str-[a-zA-Z]+");

        var result = provider.TryGetPredicate(pattern, out var predicate);

        Assert.True(result);
        Assert.NotNull(predicate);
        Assert.IsType<RegexChannelNamespacePredicate>(predicate);
        Assert.True(predicate.IsMatch("str-JumboJet"));
    }

    [Fact]
    public void UnregisteredType_Throws()
    {
        var provider = CreateProvider();
        var pattern = $"ctor:{RuntimeTypeNameFormatter.Format(typeof(FileInfo))}:C:\\temp\\299.txt";

        Assert.Throws<InvalidOperationException>(() => provider.TryGetPredicate(pattern, out _));
    }

    [Fact]
    public void UnregisteredArbitraryType_Throws()
    {
        var provider = CreateProvider();
        var pattern = $"ctor:{RuntimeTypeNameFormatter.Format(typeof(ArrayList))}";

        Assert.Throws<InvalidOperationException>(() => provider.TryGetPredicate(pattern, out _));
    }

    [Fact]
    public void UnregisteredUnregisteredType_Throws()
    {
        var provider = CreateProvider();
        var pattern = $"ctor:{RuntimeTypeNameFormatter.Format(typeof(TestGrainWithChannelPredicate))}";

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

    [RegexImplicitChannelSubscription("str-[a-zA-Z]+")]
    private class TestGrainWithRegexChannelPredicate { }

    public class TestChannelPredicate : IChannelNamespacePredicate
    {
        public string PredicatePattern => ConstructorChannelNamespacePredicateProvider.FormatPattern(typeof(TestChannelPredicate), constructorArgument: null);

        public bool IsMatch(string streamNamespace) => true;
    }
}
