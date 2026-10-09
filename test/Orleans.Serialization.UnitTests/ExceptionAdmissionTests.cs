using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Configuration;
using Orleans.Serialization.Session;
using Orleans.Serialization.TypeSystem;
using Xunit;

namespace Orleans.Serialization.UnitTests;

[Trait("Category", "BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Serialization")]
public sealed class ExceptionAdmissionTests
{
    [Fact]
    public void UnadmittedExceptionInvokesNoInitializationConstructionOrDataOverride()
    {
        using var services = CreateServices();
        var name = RuntimeTypeNameFormatter.Format(typeof(UnadmittedException));

        var result = Deserialize(services, CreatePayload(services, name));

        var fallback = Assert.IsType<UnavailableExceptionFallbackException>(result);
        Assert.Equal(name, fallback.ExceptionType);
        Assert.Equal("message", fallback.Message);
        Assert.Equal(-123, fallback.HResult);
        Assert.Contains("remote stack", fallback.StackTrace);
        Assert.Equal(0, UnadmittedEffects.Initializers);
        Assert.Equal(0, UnadmittedEffects.Constructors);
        Assert.Equal(0, UnadmittedEffects.DataGetters);
    }

    [Fact]
    public void UnavailableOuterSkipsNestedExceptionAndDataConstruction()
    {
        var factories = 0;
        using var services = CreateServices(options =>
            options.AddExceptionType(() =>
            {
                factories++;
                return new AdmittedException();
            }));
        var payload = CreatePayload(services, "Unavailable.RemoteException, Unavailable.Assembly",
            innerTypeName: RuntimeTypeNameFormatter.Format(typeof(AdmittedException)));

        var result = Assert.IsType<UnavailableExceptionFallbackException>(Deserialize(services, payload));

        Assert.Null(result.InnerException);
        Assert.Empty(result.Data);
        Assert.Equal(0, factories);
    }

    [Fact]
    public void RegisteredFactoryRestoresFrameworkAndCustomBaseProperties()
    {
        var factories = 0;
        using var services = CreateServices(options =>
            options.AddExceptionType(() =>
            {
                factories++;
                return new AdmittedException();
            }));

        var result = Assert.IsType<AdmittedException>(Deserialize(services,
            CreatePayload(services, RuntimeTypeNameFormatter.Format(typeof(AdmittedException)),
                innerTypeName: RuntimeTypeNameFormatter.Format(typeof(ArgumentException)), includeData: true)));

        Assert.Equal(1, factories);
        Assert.Equal("message", result.Message);
        Assert.Equal(-123, result.HResult);
        Assert.Contains("remote stack", result.StackTrace);
        Assert.Equal("message", Assert.IsType<ArgumentException>(result.InnerException).Message);
        Assert.Equal("value", result.Data["key"]);
    }

    [Fact]
    public void NestedUnadmittedExceptionUsesSafeFallback()
    {
        using var services = CreateServices();
        var name = RuntimeTypeNameFormatter.Format(typeof(UnadmittedException));

        var result = Deserialize(services, CreatePayload(services,
            RuntimeTypeNameFormatter.Format(typeof(InvalidOperationException)), innerTypeName: name));

        var inner = Assert.IsType<UnavailableExceptionFallbackException>(result.InnerException);
        Assert.Equal(name, inner.ExceptionType);
        Assert.Equal("message", inner.Message);
        Assert.Equal(0, UnadmittedEffects.Initializers);
        Assert.Equal(0, UnadmittedEffects.Constructors);
        Assert.Equal(0, UnadmittedEffects.DataGetters);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("System.Exception]trailing")]
    [InlineData("System.Exception[[")]
    public void MalformedExceptionNameThrowsBeforeNestedConstruction(string? name)
    {
        var factories = 0;
        using var services = CreateServices(options => options.AddExceptionType(() =>
        {
            factories++;
            return new AdmittedException();
        }));
        var payload = CreatePayload(services, name,
            innerTypeName: RuntimeTypeNameFormatter.Format(typeof(AdmittedException)));

        Assert.ThrowsAny<Exception>(() => Deserialize(services, payload));
        Assert.Equal(0, factories);
    }

    [Fact]
    public void MissingOrRepeatedExceptionNameThrowsBeforeNestedConstruction()
    {
        using var services = CreateServices();
        var name = RuntimeTypeNameFormatter.Format(typeof(Exception));

        Assert.Throws<SerializationException>(() =>
            Deserialize(services, CreatePayload(services, name, omitName: true)));
        Assert.Throws<SerializationException>(() =>
            Deserialize(services, CreatePayload(services, name, repeatName: true)));
    }

    [Fact]
    public void KnownNonExceptionThrowsBeforeNestedConstruction()
    {
        var factories = 0;
        using var services = CreateServices(options => options.AddExceptionType(() =>
        {
            factories++;
            return new AdmittedException();
        }));

        Assert.Throws<SerializationException>(() => Deserialize(services,
            CreatePayload(services, "string", innerTypeName: RuntimeTypeNameFormatter.Format(typeof(AdmittedException)))));
        Assert.Equal(0, factories);
    }

    [Fact]
    public void AggregateExceptionKeepsDedicatedCodec()
    {
        using var services = CreateServices();
        var serializer = services.GetRequiredService<Serializer>();
        var original = new AggregateException("aggregate", new InvalidOperationException("one"), new ArgumentException("two"));

        var result = Assert.IsType<AggregateException>(serializer.Deserialize<Exception>(serializer.SerializeToArray<Exception>(original)));

        Assert.Equal(2, result.InnerExceptions.Count);
        Assert.Equal("one", Assert.IsType<InvalidOperationException>(result.InnerExceptions[0]).Message);
        Assert.Equal("two", Assert.IsType<ArgumentException>(result.InnerExceptions[1]).Message);
    }

    [Fact]
    public void FactoryFailuresRemainExplicit()
    {
        using var services = CreateServices(options => options.AddExceptionType<AdmittedException>(
            () => throw new InvalidOperationException("factory failed")));

        var error = Assert.Throws<InvalidOperationException>(() => Deserialize(services,
            CreatePayload(services, RuntimeTypeNameFormatter.Format(typeof(AdmittedException)))));

        Assert.Equal("factory failed", error.Message);
    }

    [Fact]
    public void FactoryMustReturnExactRegisteredType()
    {
        using var services = CreateServices(options => options.AddExceptionType<Exception>(() => new AdmittedException()));

        Assert.Throws<SerializationException>(() => Deserialize(services,
            CreatePayload(services, RuntimeTypeNameFormatter.Format(typeof(Exception)))));
    }

    private static ServiceProvider CreateServices(Action<ExceptionSerializationOptions>? configure = null)
    {
        var services = new ServiceCollection().AddSerializer();
        services.Configure<TypeManifestOptions>(options => options.AddAllowedType(typeof(UnadmittedException)));
        if (configure is not null) services.Configure(configure);
        return services.BuildServiceProvider();
    }

    private static Exception Deserialize(ServiceProvider services, byte[] payload) =>
        services.GetRequiredService<Serializer>().Deserialize<Exception>(payload);

    private static byte[] CreatePayload(ServiceProvider services, string? name, string? innerTypeName = null,
        bool includeData = false, bool omitName = false, bool repeatName = false)
    {
        var output = new ArrayBufferWriter<byte>();
        using var session = services.GetRequiredService<SerializerSessionPool>().GetSession();
        var writer = Writer.Create(output, session);
        WriteException(ref writer, 0, name, innerTypeName, includeData, omitName, repeatName);
        writer.Commit();
        return output.WrittenSpan.ToArray();
    }

    private static void WriteException(ref Writer<ArrayBufferWriter<byte>> writer, uint delta, string? name,
        string? innerTypeName = null, bool includeData = false, bool omitName = false, bool repeatName = false)
    {
        ReferenceCodec.MarkValueField(writer.Session);
        writer.WriteStartObject(delta, typeof(Exception), typeof(ExceptionCodec));
        if (!omitName)
        {
            StringCodec.WriteField(ref writer, 0, name);
            if (repeatName) StringCodec.WriteField(ref writer, 0, name);
        }

        StringCodec.WriteField(ref writer, 1, "message");
        StringCodec.WriteField(ref writer, 1, "remote stack");
        if (innerTypeName is not null)
        {
            WriteException(ref writer, 1, innerTypeName);
        }
        else
        {
            ReferenceCodec.WriteNullReference(ref writer, 1);
        }

        Int32Codec.WriteField(ref writer, 1, -123);
        if (includeData)
        {
            var codec = writer.Session.CodecProvider.GetCodec<Dictionary<object, object?>>();
            codec.WriteField(ref writer, 1, typeof(Dictionary<object, object?>), new Dictionary<object, object?> { ["key"] = "value" });
        }

        writer.WriteEndObject();
    }

    private static class UnadmittedEffects
    {
        public static int Initializers;
        public static int Constructors;
        public static int DataGetters;
    }

    private sealed class UnadmittedException : Exception
    {
        static UnadmittedException() => UnadmittedEffects.Initializers++;
        public UnadmittedException() => UnadmittedEffects.Constructors++;
        public override IDictionary Data
        {
            get
            {
                UnadmittedEffects.DataGetters++;
                return base.Data;
            }
        }
    }

    private sealed class AdmittedException : Exception;
}
