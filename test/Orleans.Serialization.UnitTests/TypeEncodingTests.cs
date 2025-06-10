#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Serialization.Configuration;
using Orleans.Serialization.Invocation;
using Orleans.Serialization.Session;
using Orleans.Serialization.Utilities;
using Xunit;

namespace Orleans.Serialization.UnitTests
{
    /// <summary>
    /// Tests for Orleans' type encoding and type manifest system.
    /// 
    /// Orleans uses an efficient type encoding system that:
    /// - Maps types to compact identifiers for wire efficiency
    /// - Supports type aliases for human-readable debugging
    /// - Handles generic types with proper parameter encoding
    /// - Manages compound types (generics with multiple parameters)
    /// 
    /// Key features tested:
    /// - Type alias resolution and encoding
    /// - Generic type parameter serialization
    /// - Interface proxy type generation and naming
    /// - Type manifest configuration and well-known types
    /// 
    /// The type encoding system is fundamental to Orleans' ability to:
    /// - Minimize bandwidth usage through compact type representations
    /// - Support debugging through readable type aliases
    /// - Enable cross-version compatibility through stable type identifiers
    /// - Provide efficient polymorphic serialization
    /// </summary>
    public class TypeEncodingTests
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly SerializerSessionPool _sessionPool;
        private readonly Serializer _serializer;

        public TypeEncodingTests()
        {
            var services = new ServiceCollection();
            _ = services.AddSerializer();
            _serviceProvider = services.BuildServiceProvider();
            _sessionPool = _serviceProvider.GetRequiredService<SerializerSessionPool>();
            _serializer = _serviceProvider.GetRequiredService<Serializer>();
        }

        [Fact]
        public void CompoundTypeAliasesAreEncodedAsExpected()
        {
            var original = new MyCompoundTypeAliasClass
            {
                Name = "TwinkleToes",
                Value = 112
            };
            var expectedString = "(\"xx_test_xx\",[_custom_type_alias_],[int],\"1\")";
            var expectedEncoding = Encoding.UTF8.GetBytes(expectedString).AsSpan();
            var (payload, bitStream) = SerializePayload(original);
            Assert.True(payload.AsSpan().IndexOf(expectedEncoding) >= 0, $"Expected to find string \"{expectedString}\" in bitstream (formatted: {bitStream})");
        }

        [Fact]
        public void GeneratedProxyClassesHaveExpectedCompoundTypeNames()
        {
            var configuration = _serviceProvider.GetRequiredService<IOptions<TypeManifestOptions>>().Value;
            var generatedProxy = configuration.InterfaceProxies.Single(proxy => typeof(IProxyAliasTestGrain).IsAssignableFrom(proxy));
            var instance = ActivatorUtilities.CreateInstance(_serviceProvider, generatedProxy);
            var instanceAsBase = Assert.IsAssignableFrom<MyInvokableProxyBase>(instance);
            var instanceAsInterface = Assert.IsAssignableFrom<IProxyAliasTestGrain>(instance);

            var calls = new Queue<IInvokable>();
            instanceAsBase.OnInvoke = body => calls.Enqueue(body);

            {
                var res = instanceAsInterface.Method();
                Assert.True(res.IsCompletedSuccessfully);
                var method = calls.Dequeue();
                var (payload, bitStream) = SerializePayload(method);
                var expectedString = "(\"inv\",[_my_proxy_base_],[_proxy_alias_test_],\"125\")";
                var expectedEncoding = Encoding.UTF8.GetBytes(expectedString).AsSpan();
                Assert.True(payload.AsSpan().IndexOf(expectedEncoding) >= 0, $"Expected to find string \"{expectedString}\" in bitstream (formatted: {bitStream})");
            }

            {
                var res = instanceAsInterface.OtherMethod();
                Assert.True(res.IsCompletedSuccessfully);
                var method = calls.Dequeue();
                var (payload, bitStream) = SerializePayload(method);
                var expectedString = "(\"inv\",[_my_proxy_base_],[_proxy_alias_test_],\"MyOtherMethod\")";
                var expectedEncoding = Encoding.UTF8.GetBytes(expectedString).AsSpan();
                Assert.True(payload.AsSpan().IndexOf(expectedEncoding) >= 0, $"Expected to find string \"{expectedString}\" in bitstream (formatted: {bitStream})");
            }
        }

        [Fact]
        public void GeneratedProxyClassesHaveExpectedCompoundTypeNames_Generic()
        {
            var configuration = _serviceProvider.GetRequiredService<IOptions<TypeManifestOptions>>().Value;
            var generatedProxy = configuration.InterfaceProxies.Single(proxy => proxy.GetInterfaces().Any(iface => iface.IsGenericType && typeof(IGenericProxyAliasTestGrain<,,>).IsAssignableFrom(iface.GetGenericTypeDefinition())));
            var instance = ActivatorUtilities.CreateInstance(_serviceProvider, generatedProxy.MakeGenericType(typeof(int), typeof(string), typeof(double)));
            var instanceAsBase = Assert.IsAssignableFrom<MyInvokableProxyBase>(instance);
            var instanceAsInterface = Assert.IsAssignableFrom<IGenericProxyAliasTestGrain<int, string, double>>(instance);

            var calls = new Queue<IInvokable>();
            instanceAsBase.OnInvoke = body => calls.Enqueue(body);

            var res = instanceAsInterface.Method<int, MyTypeAliasClass, int>();
            Assert.True(res.IsCompletedSuccessfully);
            var method = calls.Dequeue();
            var (payload, bitStream) = SerializePayload(method);
            var expectedString = "(\"inv\",[_my_proxy_base_],[test.IGenericProxyAliasTestGrain`3],\"777\")`6[[int],[string],[double],[int],[_custom_type_alias_],[int]]";
            var expectedEncoding = Encoding.UTF8.GetBytes(expectedString).AsSpan();
            Assert.True(payload.AsSpan().IndexOf(expectedEncoding) >= 0, $"Expected to find string \"{expectedString}\" in bitstream (formatted: {bitStream})");
        }

        private (byte[] Serialized, string FormattedBitStream) SerializePayload(object original)
        {
            var payload = _serializer.SerializeToArray<object>(original);
            using var session = _sessionPool.GetSession();
            var bitStream = BitStreamFormatter.Format(payload, session);
            return (payload, bitStream);
        }

        [Fact]
        public void AliasAttributeIsApplied()
        {
            var original = new Person(2, "harry");

            var bytes = _serializer.SerializeToArray(original.GetType());
            var resultType = _serializer.Deserialize<Type>(bytes);

            var alias = original.GetType().GetCustomAttributes(false).OfType<AliasAttribute>().SingleOrDefault();
            Assert.NotNull(alias);
            Assert.NotNull(alias.Alias);
            Assert.Equal(typeof(Person), resultType);
            var expectedBytes = Encoding.UTF8.GetBytes(alias.Alias);
            Assert.True(bytes.AsSpan().IndexOf(expectedBytes) >= 0);
        }
    }

    [Alias("_custom_type_alias_")]
    public class MyTypeAliasClass
    {
    }

    [GenerateSerializer]
    public class MyCompoundTypeAliasBaseClass
    {
        [Id(0)]
        public int BaseValue { get; set; }
    }

    [GenerateSerializer]
    [CompoundTypeAlias("xx_test_xx", typeof(MyTypeAliasClass), typeof(int), "1")]
    public class MyCompoundTypeAliasClass : MyCompoundTypeAliasBaseClass
    {
        [Id(0)]
        public string? Name { get; set; }

        [Id(1)]
        public int Value { get; set; }
    }
}