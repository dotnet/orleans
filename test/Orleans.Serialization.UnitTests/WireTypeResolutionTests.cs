using System;
using System.Buffers;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Configuration;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Session;
using Orleans.Serialization.TypeSystem;
using Orleans.Serialization.WireProtocol;
using Orleans.Hosting;
using Orleans.Providers.Streams.Common;
using Xunit;

namespace Orleans.Serialization.UnitTests;

[Trait("Category", "BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Serialization")]
public sealed class WireTypeResolutionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StrictMissNeverInvokesDynamicResolverOrConverter(bool allowAll)
    {
        var resolver = new CountingResolver();
        var extension = new CountingConverter();
        var options = new TypeManifestOptions { AllowAllTypes = allowAll };
        var converter = CreateConverter(options, resolver, [extension]);
        var assemblyResolves = 0;
        ResolveEventHandler handler = (_, _) =>
        {
            assemblyResolves++;
            return null;
        };
        AppDomain.CurrentDomain.AssemblyResolve += handler;
        try
        {
            Assert.False(converter.TryParseForDeserialization("Unknown.OrleansException, Attacker.Orleans.Serialization", out _));
            Assert.False(converter.TryParseForDeserialization("(\"missing\",[Unknown.Type, Attacker.Assembly],\"v1\")", out _));
        }
        finally
        {
            AppDomain.CurrentDomain.AssemblyResolve -= handler;
        }

        Assert.Equal(0, resolver.Calls);
        Assert.Equal(0, extension.ParseCalls);
        Assert.Equal(0, assemblyResolves);
    }

    [Fact]
    public void WarmOrdinaryCacheDoesNotEstablishWireIdentity()
    {
        var options = new TypeManifestOptions { AllowAllTypes = true };
        var resolver = new CachedTypeResolver();
        var converter = CreateConverter(options, resolver);
        var name = RuntimeTypeNameFormatter.Format(typeof(UnregisteredType));

        Assert.Same(typeof(UnregisteredType), converter.Parse(name));
        Assert.Same(typeof(UnregisteredType), resolver.ResolveType(name));
        Assert.False(converter.TryParseForDeserialization(name, out _));
        Assert.False(converter.TryParseForDeserialization($"(\"missing\",[{name}])", out _));
    }

    [Fact]
    public void UnknownWireNamesDoNotPopulateAuthorizationCache()
    {
        var calls = 0;
        var converter = CreateConverter(new TypeManifestOptions(), filters:
            [new NameFilter((name, _) =>
            {
                if (name == "Unknown.Type") calls++;
                return null;
            })]);

        Assert.False(converter.TryParseForDeserialization("Unknown.Type", out _));
        Assert.False(converter.TryParseForDeserialization("Unknown.Type", out _));
        Assert.Equal(2, calls);
    }

    [Fact]
    public void AliasKnowledgeAndOrdinaryAuthorizationDoNotGrantWirePermission()
    {
        var options = new TypeManifestOptions();
        options.WellKnownTypeAliases["known_alias"] = typeof(UnregisteredType);
        var converter = CreateConverter(options);

        Assert.Same(typeof(UnregisteredType), converter.Parse("known_alias"));

        Assert.Throws<InvalidOperationException>(() => converter.ParseForDeserialization("known_alias"));
    }

    [Fact]
    public void FrameworkNamesAndOrleansLikeAssembliesDoNotGrantSpoofedIdentities()
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName($"Spoof.Orleans.Serialization.{Guid.NewGuid():N}"), AssemblyBuilderAccess.Run);
        var spoofed = assembly.DefineDynamicModule("types")
            .DefineType("System.Int32", TypeAttributes.Public).CreateTypeInfo()!.AsType();
        var options = new TypeManifestOptions();
        options.WellKnownTypeAliases["spoofed_integer"] = spoofed;
        var converter = CreateConverter(options);

        Assert.Same(typeof(int), converter.ParseForDeserialization("int"));
        Assert.Same(typeof(int), converter.ParseForDeserialization("System.Int32"));
        Assert.Throws<InvalidOperationException>(() => converter.ParseForDeserialization("spoofed_integer"));
        Assert.Throws<InvalidOperationException>(() => converter.ParseForDeserialization(
            RuntimeTypeNameFormatter.Format(spoofed)));
    }

    [Fact]
    public void CompoundAliasComponentsAndTargetRequireIndependentAdmission()
    {
        var options = new TypeManifestOptions();
        options.AddAllowedType(typeof(RegisteredType));
        options.WellKnownTypeAliases["component"] = typeof(UnregisteredType);
        options.CompoundTypeAliases.Add("alias").Add(typeof(UnregisteredType), typeof(RegisteredType));
        var converter = CreateConverter(options);

        Assert.Throws<InvalidOperationException>(() => converter.ParseForDeserialization("(\"alias\",[component])"));

        options.AddAllowedType(typeof(UnregisteredType));
        converter = CreateConverter(options);
        Assert.Same(typeof(RegisteredType), converter.ParseForDeserialization("(\"alias\",[component])"));
    }

    [Fact]
    public void CompleteGraphDenialPrecedesBindingAnInvalidOuterAlias()
    {
        var resolver = new CountingResolver();
        var converter = CreateConverter(new TypeManifestOptions(), resolver,
            filters: [new NameFilter((name, _) => name == "Denied.Type" ? false : null)]);

        Assert.Throws<InvalidOperationException>(() => converter.ParseForDeserialization(
            "(\"missing\",[Unknown.Type, Attacker.Assembly],[Denied.Type, Denied.Assembly])"));
        Assert.Equal(0, resolver.Calls);
    }

    [Fact]
    public void ExplicitTypeRegistrationRemainsAuthoritativeWhileAssemblyTrustHonorsDenials()
    {
        var direct = new TypeManifestOptions();
        direct.AddAllowedType(typeof(RegisteredType));
        var filter = new NameFilter((name, _) => name == typeof(RegisteredType).FullName ? false : null);
        Assert.Same(typeof(RegisteredType), CreateConverter(direct, filters: [filter]).ParseForDeserialization(
            RuntimeTypeNameFormatter.Format(typeof(RegisteredType))));

        var assembly = new TypeManifestOptions();
        assembly.AddAllowedAssembly(typeof(RegisteredType).Assembly);
        Assert.Throws<InvalidOperationException>(() => CreateConverter(assembly, filters: [filter]).ParseForDeserialization(
            RuntimeTypeNameFormatter.Format(typeof(RegisteredType))));
    }

    [Fact]
    public void MetadataPermissionHonorsExplicitNameAndResolvedTypeDenials()
    {
        var options = new TypeManifestOptions();
        options.AddSerializer<RegisteredType>(_ => throw new NotSupportedException(), _ => throw new NotSupportedException());
        var name = RuntimeTypeNameFormatter.Format(typeof(RegisteredType));
        var named = CreateConverter(options, filters: [new NameFilter((candidate, _) =>
            candidate == typeof(RegisteredType).FullName ? false : null)]);
        var typed = CreateConverter(options, typeFilters: [new TypeFilter(candidate =>
            candidate == typeof(RegisteredType) ? false : null)]);

        Assert.Throws<InvalidOperationException>(() => named.ParseForDeserialization(name));
        Assert.Throws<InvalidOperationException>(() => typed.ParseForDeserialization(name));
    }

    [Fact]
    public void GenericDefinitionAndEveryArgumentAreAuthorizedBeforeConstruction()
    {
        var options = new TypeManifestOptions();
        options.WellKnownTypeAliases["generic`1"] = typeof(UnregisteredGeneric<>);
        var converter = CreateConverter(options);
        Assert.Throws<InvalidOperationException>(() => converter.ParseForDeserialization("generic`1[[int]]"));

        options.AddAllowedType(typeof(UnregisteredGeneric<>));
        options.WellKnownTypeAliases["unknown_argument"] = typeof(UnregisteredType);
        converter = CreateConverter(options);
        Assert.Throws<InvalidOperationException>(() => converter.ParseForDeserialization("generic`1[[unknown_argument]]"));

        options.AddAllowedType(typeof(UnregisteredType));
        converter = CreateConverter(options);
        Assert.Same(typeof(UnregisteredGeneric<UnregisteredType>), converter.ParseForDeserialization("generic`1[[unknown_argument]]"));
    }

    [Fact]
    public void DefaultEncodedLengthPrefixedAndDiagnosticReadersUseStrictBoundary()
    {
        var resolver = new CountingResolver();
        var converter = CreateConverter(new TypeManifestOptions(), resolver);
        var codec = new TypeCodec(converter);
        using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var pool = services.GetRequiredService<SerializerSessionPool>();
        var bytes = Encoding.UTF8.GetBytes("Unknown.OrleansException, Attacker.Orleans.Serialization");
        var output = new ArrayBufferWriter<byte>();
        using (var session = pool.GetSession())
        {
            var writer = Writer.Create(output, session);
            writer.WriteByte(1);
            writer.WriteInt32(1234);
            writer.WriteVarUInt32((uint)bytes.Length);
            writer.Write(bytes);
            writer.Commit();
        }

        using (var session = pool.GetSession())
        {
            var reader = Reader.Create(output.WrittenMemory, session);
            Assert.Null(codec.TryRead(ref reader));
        }

        using (var session = pool.GetSession())
        {
            var reader = Reader.Create(output.WrittenMemory, session);
            Assert.False(codec.TryReadForAnalysis(ref reader, out _, out _));
        }

        var lengthPrefixed = output.WrittenMemory[5..];
        Assert.Throws<TypeLoadException>(() => ReadLengthPrefixed(lengthPrefixed));
        Assert.Equal(0, resolver.Calls);

        void ReadLengthPrefixed(ReadOnlyMemory<byte> payload)
        {
            using var session = pool.GetSession();
            var reader = Reader.Create(payload, session);
            codec.ReadLengthPrefixed(ref reader);
        }
    }

    [Fact]
    public void UnavailableUnknownFieldCanBeSkippedButRequiredRootFailsExplicitly()
    {
        using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var pool = services.GetRequiredService<SerializerSessionPool>();
        var output = new ArrayBufferWriter<byte>();
        using (var session = pool.GetSession())
        {
            var writer = Writer.Create(output, session);
            var name = Encoding.UTF8.GetBytes("Unknown.FutureType, Unknown.Assembly");
            ReferenceCodec.MarkValueField(session);
            writer.WriteByte((byte)((uint)WireType.TagDelimited | (uint)SchemaType.Encoded));
            writer.WriteByte(1);
            writer.WriteInt32(123);
            writer.WriteVarUInt32((uint)name.Length);
            writer.Write(name);
            writer.WriteEndObject();
            Int32Codec.WriteField(ref writer, 1, 29);
            writer.Commit();
        }

        using (var session = pool.GetSession())
        {
            var reader = Reader.Create(output.WrittenMemory, session);
            var unknown = reader.ReadFieldHeader();
            Assert.Null(unknown.FieldType);
            Assert.Equal(SchemaType.Encoded, unknown.Tag.SchemaType);
            reader.ConsumeUnknownField(unknown);
            Assert.Equal(29, Int32Codec.ReadValue(ref reader, reader.ReadFieldHeader()));
            Assert.Equal(2u, session.ReferencedObjects.CurrentReferenceId);
        }

        Assert.Throws<TypeMissingException>(() =>
            services.GetRequiredService<Serializer>().Deserialize<object>(output.WrittenSpan.ToArray()));
        Assert.Throws<TypeMissingException>(() =>
            services.GetRequiredService<Serializer<object>>().Deserialize(output.WrittenSpan.ToArray()));
    }

    [Fact]
    public void RegisteredClosedContextCollectionsAndManifestAliasesRoundTrip()
    {
        using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var converter = services.GetRequiredService<TypeConverter>();
        var serializer = services.GetRequiredService<Serializer>();
        var value = new List<int> { 13, 17 };

        Assert.Same(typeof(List<int>), converter.ParseForDeserialization(converter.Format(typeof(List<int>))));
        Assert.Equal(value, Assert.IsType<List<int>>(serializer.Deserialize<object>(serializer.SerializeToArray<object>(value))));
        Assert.Same(typeof(Orleans.Serialization.ExceptionCodec), converter.ParseForDeserialization("Exception"));
        var dictionary = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["key"] = 29 };
        var dictionaryResult = Assert.IsType<Dictionary<string, int>>(
            serializer.Deserialize<object>(serializer.SerializeToArray<object>(dictionary)));
        Assert.Equal(29, dictionaryResult["KEY"]);

        using var contextServices = new ServiceCollection().AddSerializerContext(new WirePayloadContext()).BuildServiceProvider();
        var contextConverter = contextServices.GetRequiredService<TypeConverter>();
        var contextSerializer = contextServices.GetRequiredService<Serializer>();
        var closedType = typeof(List<WirePayload>);
        Assert.Same(closedType, contextConverter.ParseForDeserialization(contextConverter.Format(closedType)));
        var original = new List<WirePayload> { new() { Value = 43 } };
        var restored = Assert.IsType<List<WirePayload>>(
            contextSerializer.Deserialize<object>(contextSerializer.SerializeToArray<object>(original)));
        Assert.Equal(43, Assert.Single(restored).Value);
        Assert.NotSame(original, restored);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StreamingHostingRegistersProviderControlMarkerIdentity(bool silo)
    {
        var registrations = new ServiceCollection().AddSerializer();
        if (silo) registrations.AddSiloStreaming();
        else registrations.AddClientStreaming();
        using var services = registrations.BuildServiceProvider();
        var serializer = services.GetRequiredService<Serializer>();
        var providerType = typeof(PersistentStreamProvider);

        Assert.Same(providerType,
            serializer.Deserialize<Type>(serializer.SerializeToArray<Type>(providerType)));
    }

    [Theory]
    [InlineData("string]trailing")]
    [InlineData("string[")]
    [InlineData("[[")]
    [InlineData(" ")]
    public void MalformedCompleteNameRemainsAnExplicitError(string name)
    {
        Assert.ThrowsAny<Exception>(() => CreateConverter(new TypeManifestOptions()).ParseForDeserialization(name));
    }

    private static TypeConverter CreateConverter(TypeManifestOptions options, TypeResolver? resolver = null,
        ITypeConverter[]? converters = null, ITypeNameFilter[]? filters = null, ITypeFilter[]? typeFilters = null) =>
        new(converters ?? [], filters ?? [new DefaultTypeFilter()], typeFilters ?? [], Options.Create(options), resolver ?? new CachedTypeResolver());

    private sealed class CountingResolver : TypeResolver
    {
        public int Calls;
        public override Type ResolveType(string name)
        {
            Calls++;
            return typeof(UnregisteredType);
        }
        public override bool TryResolveType(string name, out Type type)
        {
            Calls++;
            type = typeof(UnregisteredType);
            return true;
        }
    }

    private sealed class CountingConverter : ITypeConverter
    {
        public int ParseCalls;
        public bool TryFormat(Type type, out string formatted)
        {
            formatted = string.Empty;
            return false;
        }
        public bool TryParse(string formatted, out Type type)
        {
            ParseCalls++;
            type = typeof(UnregisteredType);
            return true;
        }
    }

    private sealed class NameFilter(Func<string, string, bool?> filter) : ITypeNameFilter
    {
        public bool? IsTypeNameAllowed(string typeName, string assemblyName) => filter(typeName, assemblyName);
    }

    private sealed class TypeFilter(Func<Type, bool?> filter) : ITypeFilter
    {
        public bool? IsTypeAllowed(Type type) => filter(type);
    }

    private sealed class RegisteredType;
    private sealed class UnregisteredType;
    private sealed class UnregisteredGeneric<T>;
}

[GenerateSerializer]
[CompoundTypeAlias("wire-payload")]
internal sealed class WirePayload
{
    [Id(0)]
    public int Value { get; set; }
}

[GenerateSerializerContext<List<WirePayload>>]
internal partial class WirePayloadContext : SerializerContext;
