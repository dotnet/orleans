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
        var provider = CreateProvider();
        provider.RegisterPredicateType(typeof(TestStreamPredicateWithArg));
        var pattern = ConstructorStreamNamespacePredicateProvider.FormatPattern(typeof(TestStreamPredicateWithArg), constructorArgument: "test-ns");

        var result = provider.TryGetPredicate(pattern, out var predicate);

        Assert.True(result);
        Assert.NotNull(predicate);
        Assert.IsType<TestStreamPredicateWithArg>(predicate);
        Assert.True(predicate.IsMatch("test-ns"));
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

    [ImplicitStreamSubscription(typeof(TestStreamPredicate))]
    private class TestGrainWithStreamPredicate { }

    

    public class TestStreamPredicate : IStreamNamespacePredicate
    {
        public string PredicatePattern => ConstructorStreamNamespacePredicateProvider.FormatPattern(typeof(TestStreamPredicate), constructorArgument: null);

        public bool IsMatch(string streamNamespace) => true;
    }

    public class TestStreamPredicateWithArg : IStreamNamespacePredicate
    {
        private readonly string _namespace;

        public TestStreamPredicateWithArg(string ns) => _namespace = ns;

        public string PredicatePattern => ConstructorStreamNamespacePredicateProvider.FormatPattern(typeof(TestStreamPredicateWithArg), _namespace);

        public bool IsMatch(string streamNamespace) => string.Equals(_namespace, streamNamespace, StringComparison.Ordinal);
    }
}
