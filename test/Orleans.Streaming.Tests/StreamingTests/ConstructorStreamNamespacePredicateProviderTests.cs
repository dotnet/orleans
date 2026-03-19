using System;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Serialization.TypeSystem;
using Orleans.Streams;
using Xunit;

namespace UnitTests;

/// <summary>
/// Tests for <see cref="ConstructorStreamNamespacePredicateProvider"/> predicate type registration and resolution.
/// </summary>
[TestCategory("BVT"), TestCategory("Predicates")]
public class ConstructorStreamNamespacePredicateProviderTests
{
    private static ConstructorStreamNamespacePredicateProvider CreateProvider(params Type[] grainClasses)
    {
        var options = new GrainTypeOptions();
        foreach (var grainClass in grainClasses)
        {
            options.Classes.Add(grainClass);
        }

        return new ConstructorStreamNamespacePredicateProvider(Options.Create(options));
    }

    [Fact]
    public void RegisteredPredicateType_Succeeds()
    {
        var provider = CreateProvider(typeof(TestGrainWithStreamPredicate));
        var pattern = ConstructorStreamNamespacePredicateProvider.FormatPattern(typeof(TestStreamPredicate), constructorArgument: null);

        var result = provider.TryGetPredicate(pattern, out var predicate);

        Assert.True(result);
        Assert.NotNull(predicate);
        Assert.IsType<TestStreamPredicate>(predicate);
    }

    [Fact]
    public void RegisteredPredicateTypeWithArg_Succeeds()
    {
        var provider = CreateProvider(typeof(TestGrainWithRegexStreamPredicate));
        var pattern = ConstructorStreamNamespacePredicateProvider.FormatPattern(typeof(RegexStreamNamespacePredicate), constructorArgument: "str-[a-zA-Z]+");

        var result = provider.TryGetPredicate(pattern, out var predicate);

        Assert.True(result);
        Assert.NotNull(predicate);
        Assert.IsType<RegexStreamNamespacePredicate>(predicate);
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
        var pattern = $"ctor:{RuntimeTypeNameFormatter.Format(typeof(System.Collections.ArrayList))}";

        Assert.Throws<InvalidOperationException>(() => provider.TryGetPredicate(pattern, out _));
    }

    [Fact]
    public void UnregisteredPredicateType_Throws()
    {
        var provider = CreateProvider();
        var pattern = $"ctor:{RuntimeTypeNameFormatter.Format(typeof(TestStreamPredicate))}";

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

    [ImplicitStreamSubscription(typeof(TestStreamPredicate))]
    private class TestGrainWithStreamPredicate { }

    public class TestStreamPredicate : IStreamNamespacePredicate
    {
        public string PredicatePattern => ConstructorStreamNamespacePredicateProvider.FormatPattern(typeof(TestStreamPredicate), constructorArgument: null);

        public bool IsMatch(string streamNamespace) => true;
    }

    [RegexImplicitStreamSubscription("str-[a-zA-Z]+")]
    private class TestGrainWithRegexStreamPredicate { }
}
