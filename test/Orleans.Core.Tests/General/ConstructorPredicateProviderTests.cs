using System;
using Orleans.BroadcastChannel;
using Orleans.Serialization.TypeSystem;
using Orleans.Streams;
using Xunit;

namespace UnitTests;

/// <summary>
/// Tests for <see cref="ConstructorStreamNamespacePredicateProvider"/> and
/// <see cref="ConstructorChannelNamespacePredicateProvider"/> predicate type registration and resolution.
/// </summary>
[TestCategory("BVT"), TestCategory("Predicates")]
public class ConstructorPredicateProviderTests
{
    [Fact]
    public void StreamProvider_RegisteredPredicateType_Succeeds()
    {
        var provider = new ConstructorStreamNamespacePredicateProvider();
        provider.RegisterPredicateType(typeof(TestStreamPredicate));
        var pattern = ConstructorStreamNamespacePredicateProvider.FormatPattern(typeof(TestStreamPredicate), constructorArgument: null);

        var result = provider.TryGetPredicate(pattern, out var predicate);

        Assert.True(result);
        Assert.NotNull(predicate);
        Assert.IsType<TestStreamPredicate>(predicate);
    }

    [Fact]
    public void StreamProvider_RegisteredPredicateTypeWithArg_Succeeds()
    {
        var provider = new ConstructorStreamNamespacePredicateProvider();
        provider.RegisterPredicateType(typeof(TestStreamPredicateWithArg));
        var pattern = ConstructorStreamNamespacePredicateProvider.FormatPattern(typeof(TestStreamPredicateWithArg), constructorArgument: "test-ns");

        var result = provider.TryGetPredicate(pattern, out var predicate);

        Assert.True(result);
        Assert.NotNull(predicate);
        Assert.IsType<TestStreamPredicateWithArg>(predicate);
        Assert.True(predicate.IsMatch("test-ns"));
    }

    [Fact]
    public void StreamProvider_UnregisteredType_Throws()
    {
        var provider = new ConstructorStreamNamespacePredicateProvider();
        var maliciousPattern = $"ctor:{RuntimeTypeNameFormatter.Format(typeof(System.IO.FileInfo))}:C:\\temp\\evil.txt";

        Assert.Throws<InvalidOperationException>(() => provider.TryGetPredicate(maliciousPattern, out _));
    }

    [Fact]
    public void StreamProvider_UnregisteredArbitraryType_Throws()
    {
        var provider = new ConstructorStreamNamespacePredicateProvider();
        var maliciousPattern = $"ctor:{RuntimeTypeNameFormatter.Format(typeof(System.Collections.ArrayList))}";

        Assert.Throws<InvalidOperationException>(() => provider.TryGetPredicate(maliciousPattern, out _));
    }

    [Fact]
    public void StreamProvider_NonMatchingPrefix_ReturnsFalse()
    {
        var provider = new ConstructorStreamNamespacePredicateProvider();

        var result = provider.TryGetPredicate("namespace:test", out var predicate);

        Assert.False(result);
        Assert.Null(predicate);
    }

    [Fact]
    public void ChannelProvider_RegisteredPredicateType_Succeeds()
    {
        var provider = new ConstructorChannelNamespacePredicateProvider();
        provider.RegisterPredicateType(typeof(TestChannelPredicate));
        var pattern = ConstructorChannelNamespacePredicateProvider.FormatPattern(typeof(TestChannelPredicate), constructorArgument: null);

        var result = provider.TryGetPredicate(pattern, out var predicate);

        Assert.True(result);
        Assert.NotNull(predicate);
        Assert.IsType<TestChannelPredicate>(predicate);
    }

    [Fact]
    public void ChannelProvider_RegisteredPredicateTypeWithArg_Succeeds()
    {
        var provider = new ConstructorChannelNamespacePredicateProvider();
        provider.RegisterPredicateType(typeof(TestChannelPredicateWithArg));
        var pattern = ConstructorChannelNamespacePredicateProvider.FormatPattern(typeof(TestChannelPredicateWithArg), constructorArgument: "ch-ns");

        var result = provider.TryGetPredicate(pattern, out var predicate);

        Assert.True(result);
        Assert.NotNull(predicate);
        Assert.IsType<TestChannelPredicateWithArg>(predicate);
        Assert.True(predicate.IsMatch("ch-ns"));
    }

    [Fact]
    public void ChannelProvider_UnregisteredType_Throws()
    {
        var provider = new ConstructorChannelNamespacePredicateProvider();
        var maliciousPattern = $"ctor:{RuntimeTypeNameFormatter.Format(typeof(System.IO.FileInfo))}:C:\\temp\\evil.txt";

        Assert.Throws<InvalidOperationException>(() => provider.TryGetPredicate(maliciousPattern, out _));
    }

    [Fact]
    public void ChannelProvider_UnregisteredArbitraryType_Throws()
    {
        var provider = new ConstructorChannelNamespacePredicateProvider();
        var maliciousPattern = $"ctor:{RuntimeTypeNameFormatter.Format(typeof(System.Collections.ArrayList))}";

        Assert.Throws<InvalidOperationException>(() => provider.TryGetPredicate(maliciousPattern, out _));
    }

    [Fact]
    public void ChannelProvider_NonMatchingPrefix_ReturnsFalse()
    {
        var provider = new ConstructorChannelNamespacePredicateProvider();

        var result = provider.TryGetPredicate("namespace:test", out var predicate);

        Assert.False(result);
        Assert.Null(predicate);
    }

    public class TestStreamPredicate: IStreamNamespacePredicate
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
