using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Configuration;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.TypeSystem;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.IO;

namespace Orleans.CodeGenerator.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("CodeGen")]
public sealed class SerializerInitializationTests
{
    [Fact]
    public void AddingAutomaticServicesAfterContextInitializesThemOnce()
    {
        var services = new ServiceCollection()
            .AddGeneratedContext(new ManualContext())
            .AddSerializer()
            .AddSerializer();
        Assert.Equal(3, services.Count(descriptor => descriptor.ServiceType == typeof(IGeneralizedCodec)));
        using var provider = services.BuildServiceProvider();
        Assert.IsType<CachedTypeResolver>(provider.GetRequiredService<TypeResolver>());
        Assert.IsType<StringCodec>(provider.GetRequiredService<CodecProvider>().GetCodec<string>());
    }

    [Fact]
    public void ContextServicesUseTheCommonTypeResolver()
    {
        using var provider = new ServiceCollection().AddGeneratedContext(new ManualContext()).BuildServiceProvider();
        var resolver = provider.GetRequiredService<TypeResolver>();
        Assert.IsType<CachedTypeResolver>(resolver);
        Assert.Equal(typeof(int), resolver.ResolveType("System.Int32"));
        Assert.Equal(typeof(string), resolver.ResolveType("System.String"));
        Assert.False(resolver.TryResolveType("Missing.Serialization.Type", out _));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task GeneratedMetadataRejectsConflictingTypesInEitherOrder(bool alias, bool reverse)
    {
        var dictionary = alias ? "options.WellKnownTypeAliases" : "options.WellKnownTypeIds";
        var key = alias ? "\"conflict-key\"" : "1042U";
        var first = WriteRegistration(dictionary, key, reverse ? "typeof(string)" : "typeof(int)");
        var second = WriteRegistration(dictionary, key, reverse ? "typeof(int)" : "typeof(string)");
        var compilation = await TestCompilationHelper.CreateCompilation($$"""
            public static class MetadataConflictProof
            {
                public static string Run()
                {
                    var options = new Orleans.Serialization.Configuration.TypeManifestOptions();
                    {{first}}
                    try
                    {
                        {{second}}
                    }
                    catch (System.InvalidOperationException exception)
                    {
                        return exception.Message;
                    }
                    throw new System.Exception("Expected conflicting metadata to be rejected.");
                }
            }
            """, $"MetadataConflictProof{Guid.NewGuid():N}");
        using var image = new MemoryStream();
        var emit = compilation.Emit(image, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics));
        var assembly = System.Reflection.Assembly.Load(image.ToArray());
        var message = Assert.IsType<string>(assembly.GetType("MetadataConflictProof")!.GetMethod("Run")!.Invoke(null, null));
        Assert.Contains("Conflicting type metadata registration", message, StringComparison.Ordinal);
        Assert.Contains(alias ? "conflict-key" : "1042", message, StringComparison.Ordinal);
    }

    private sealed class ManualContext : GeneratedContext
    {
        protected override void ConfigureInner(TypeManifestOptions options)
            => options.AddSerializer<int>(static _ => new Int32Codec(), static _ => new ShallowCopier<int>());
    }

    private static string WriteRegistration(string dictionary, string key, string type)
    {
        var writer = new SourceWriter();
        MetadataGenerator.WriteTypeMetadataRegistration(writer, dictionary, key, type);
        return writer.ToString();
    }
}
