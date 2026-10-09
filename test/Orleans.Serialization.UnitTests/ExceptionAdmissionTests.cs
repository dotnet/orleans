using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.Serialization;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Configuration;
using Orleans.Serialization.Session;
using Orleans.Serialization.TypeSystem;
using Orleans.Serialization.WireProtocol;
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
    public void RegisteredCustomFactoryRoundTripsThroughDefaultCodecSelection()
    {
        var factories = 0;
        using var services = CreateServices(options => options.AddExceptionType(() =>
        {
            factories++;
            return new AdmittedException();
        }));
        var original = new AdmittedException();
        services.GetRequiredService<ExceptionCodec>().SetBaseProperties(original, "custom", null,
            new ArgumentException("inner"), -456, new Dictionary<object, object?> { ["key"] = "value" });
        var serializer = services.GetRequiredService<Serializer>();

        var result = Assert.IsType<AdmittedException>(
            serializer.Deserialize<Exception>(serializer.SerializeToArray<Exception>(original)));

        Assert.Equal(1, factories);
        Assert.Equal("custom", result.Message);
        Assert.Equal(-456, result.HResult);
        Assert.Equal("inner", Assert.IsType<ArgumentException>(result.InnerException).Message);
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

    [Fact]
    public void DataNestedExceptionUsesAdmissionBeforeConstruction()
    {
        using var services = CreateServices();
        var name = RuntimeTypeNameFormatter.Format(typeof(UnadmittedException));
        var payload = CreatePayload(services, RuntimeTypeNameFormatter.Format(typeof(Exception)), dataTypeName: name);

        var result = Deserialize(services, payload);

        var nested = Assert.IsType<UnavailableExceptionFallbackException>(result.Data["key"]);
        Assert.Equal(name, nested.ExceptionType);
        Assert.Equal(0, UnadmittedEffects.Initializers);
        Assert.Equal(0, UnadmittedEffects.Constructors);
        Assert.Equal(0, UnadmittedEffects.DataGetters);
    }

    [Fact]
    public void DiscardedDataCannotBeRevivedByLaterReference()
    {
        var factories = 0;
        using var services = CreateServices(options => options.AddExceptionType(() =>
        {
            factories++;
            return new AdmittedException();
        }));
        var output = new ArrayBufferWriter<byte>();
        var pool = services.GetRequiredService<SerializerSessionPool>();
        uint expectedReferences;
        using (var session = pool.GetSession())
        {
            var writer = Writer.Create(output, session);
            var reference = WriteException(ref writer, 0, "Unknown.RemoteException, Unknown.Assembly",
                dataTypeName: RuntimeTypeNameFormatter.Format(typeof(AdmittedException)));
            Assert.NotEqual(0u, reference);
            ReferenceCodec.MarkValueField(session);
            writer.WriteFieldHeader(0, typeof(object), typeof(Dictionary<object, object?>), WireType.Reference);
            writer.WriteVarUInt32(reference);
            writer.Commit();
            expectedReferences = session.ReferencedObjects.CurrentReferenceId;
        }

        using var readerSession = pool.GetSession();
        var reader = Reader.Create(output.WrittenMemory, readerSession);
        var field = reader.ReadFieldHeader();
        var codec = services.GetRequiredService<ExceptionCodec>();
        var fallback = Assert.IsType<UnavailableExceptionFallbackException>(codec.DeserializeException(ref reader, field));
        Assert.Empty(fallback.Data);
        var referenceField = reader.ReadFieldHeader();
        var error = ReadReferenceError(output.WrittenMemory);
        Assert.IsType<ReferenceNotFoundException>(error);
        Assert.Equal(0, factories);
        Assert.Equal(expectedReferences - 1, readerSession.ReferencedObjects.CurrentReferenceId);

        Exception ReadReferenceError(ReadOnlyMemory<byte> bytes)
        {
            using var session = pool.GetSession();
            var input = Reader.Create(bytes, session);
            var outer = input.ReadFieldHeader();
            codec.DeserializeException(ref input, outer);
            var reference = input.ReadFieldHeader();
            try
            {
                ReferenceCodec.ReadReference(ref input, reference.FieldType);
            }
            catch (ReferenceNotFoundException exception)
            {
                Assert.Equal(expectedReferences, session.ReferencedObjects.CurrentReferenceId);
                Assert.Equal(bytes.Length, input.Position);
                return exception;
            }

            throw new InvalidOperationException("The discarded dictionary was revived.");
        }
    }

    [Fact]
    public void AdmittedExceptionNameDenialIsExplicitBeforeNestedFactories()
    {
        var factories = 0;
        var registrations = new ServiceCollection().AddSerializer();
        registrations.Configure<ExceptionSerializationOptions>(options => options.AddExceptionType(() =>
        {
            factories++;
            return new AdmittedException();
        }));
        registrations.AddSingleton<ITypeNameFilter>(new DenyingExceptionNameFilter());
        using var services = registrations.BuildServiceProvider();
        var name = RuntimeTypeNameFormatter.Format(typeof(AdmittedException));

        var error = Assert.Throws<InvalidOperationException>(() => Deserialize(services,
            CreatePayload(services, name, innerTypeName: name)));

        Assert.Contains(nameof(ITypeNameFilter), error.Message);
        Assert.Equal(0, factories);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TypeAndTypeNameReferencesCannotConstructAnUnknownFieldMarkerTarget(bool typeValue)
    {
        var factories = 0;
        using var services = CreateServices(options => options.AddExceptionType(() =>
        {
            factories++;
            return new AdmittedException();
        }));
        var output = new ArrayBufferWriter<byte>();
        var pool = services.GetRequiredService<SerializerSessionPool>();
        using (var session = pool.GetSession())
        {
            var writer = Writer.Create(output, session);
            WriteException(ref writer, 0, RuntimeTypeNameFormatter.Format(typeof(AdmittedException)));
            if (!typeValue)
            {
                ReferenceCodec.MarkValueField(session);
                writer.WriteStartObject(0, typeof(Exception), typeof(ExceptionCodec));
            }

            ReferenceCodec.MarkValueField(session);
            writer.WriteFieldHeaderExpected(0, WireType.Reference);
            writer.WriteVarUInt32(1);
            if (!typeValue) writer.WriteEndObject();
            writer.Commit();
        }

        var error = Assert.Throws<InvalidCastException>(ReadPayload);
        Assert.Contains("not a serialized", error.Message);
        Assert.Equal(0, factories);

        void ReadPayload()
        {
            using var session = pool.GetSession();
            var reader = Reader.Create(output.WrittenMemory, session);
            reader.ConsumeUnknownField(reader.ReadFieldHeader());
            var field = reader.ReadFieldHeader();
            if (typeValue) TypeSerializerCodec.ReadValue(ref reader, field);
            else services.GetRequiredService<ExceptionCodec>().DeserializeException(ref reader, field);
        }
    }

    [Fact]
    public void CorruptUtf8ExceptionNameRemainsAnExplicitError()
    {
        using var services = CreateServices();
        var output = new ArrayBufferWriter<byte>();
        using (var session = services.GetRequiredService<SerializerSessionPool>().GetSession())
        {
            var writer = Writer.Create(output, session);
            ReferenceCodec.MarkValueField(session);
            writer.WriteStartObject(0, typeof(Exception), typeof(ExceptionCodec));
            ReferenceCodec.MarkValueField(session);
            writer.WriteFieldHeaderExpected(0, WireType.LengthPrefixed);
            writer.WriteVarUInt32(1);
            writer.WriteByte(0xFF);
            writer.WriteEndObject();
            writer.Commit();
        }

        Assert.Throws<DecoderFallbackException>(() => Deserialize(services, output.WrittenSpan.ToArray()));
    }

    [Fact]
    public void CorruptDiscardedGraphRemainsAnExplicitError()
    {
        var factories = 0;
        using var services = CreateServices(options => options.AddExceptionType(() =>
        {
            factories++;
            return new AdmittedException();
        }));
        var payload = CreatePayload(services, "Unknown.RemoteException, Unknown.Assembly",
            dataTypeName: RuntimeTypeNameFormatter.Format(typeof(AdmittedException)));

        var error = Assert.Throws<InvalidOperationException>(() => Deserialize(services, payload[..^1]));
        Assert.Contains("Insufficient data", error.Message);
        Assert.Equal(0, factories);
    }

    [Theory]
    [InlineData(typeof(AbstractException))]
    [InlineData(typeof(GenericException<>))]
    [InlineData(typeof(AggregateException))]
    public void EmbeddedExceptionRequiresConcreteClosedDedicatedCompatibleType(Type type)
    {
        var registrations = new ServiceCollection().AddSerializer();
        registrations.Configure<TypeManifestOptions>(options => options.AddAllowedType(type));
        using var services = registrations.BuildServiceProvider();

        Assert.Throws<SerializationException>(() => Deserialize(services,
            CreatePayload(services, RuntimeTypeNameFormatter.Format(type))));
    }

    [Fact]
    public void SerializableUnavailableExceptionDiscardsNestedConstruction()
    {
        var factories = 0;
        using var services = CreateServices(options => options.AddExceptionType(() =>
        {
            factories++;
            return new AdmittedException();
        }), dotNetSerializable: true);
        var result = Assert.IsType<UnavailableExceptionFallbackException>(
            Deserialize(services, CreateSerializablePayload(services, "Unknown.RemoteException, Unknown.Assembly")));

        Assert.Equal("Unknown.RemoteException, Unknown.Assembly", result.ExceptionType);
        Assert.Equal("message", result.Message);
        Assert.Equal(-123, result.HResult);
        Assert.Null(result.InnerException);
        Assert.Empty(result.Data);
        Assert.DoesNotContain("Data", result.Properties.Keys);
        Assert.Equal(0, factories);
    }

    [Fact]
    public void SerializableKnownNonExceptionFailsBeforeNestedConstruction()
    {
        var factories = 0;
        using var services = CreateServices(options => options.AddExceptionType(() =>
        {
            factories++;
            return new AdmittedException();
        }), dotNetSerializable: true);

        Assert.Throws<SerializationException>(() =>
            Deserialize(services, CreateSerializablePayload(services, "string")));
        Assert.Equal(0, factories);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("System.Exception]trailing")]
    [InlineData("System.Exception[[")]
    [InlineData("System.Exception, Unknown.Assembly, Version=invalid")]
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

    private static ServiceProvider CreateServices(Action<ExceptionSerializationOptions>? configure = null, bool dotNetSerializable = false)
    {
        var services = new ServiceCollection().AddSerializer();
        services.Configure<TypeManifestOptions>(options => options.AddAllowedType(typeof(UnadmittedException)));
        if (configure is not null) services.Configure(configure);
        if (dotNetSerializable) services.AddSingleton<Serializers.IGeneralizedCodec, DotNetSerializableCodec>();
        return services.BuildServiceProvider();
    }

    private static Exception Deserialize(ServiceProvider services, byte[] payload) =>
        Assert.IsAssignableFrom<Exception>(services.GetRequiredService<Serializer>().Deserialize<Exception>(payload));

    private static byte[] CreateSerializablePayload(ServiceProvider services, string name)
    {
        var output = new ArrayBufferWriter<byte>();
        using var session = services.GetRequiredService<SerializerSessionPool>().GetSession();
        var writer = Writer.Create(output, session);
        ReferenceCodec.CreateRecordPlaceholder(session);
        writer.WriteStartObject(0, typeof(Exception), typeof(DotNetSerializableCodec));
        StringCodec.WriteField(ref writer, 1, name);
        var entries = new SerializationEntryCodec();
        entries.WriteField(ref writer, 1, typeof(SerializationEntrySurrogate),
            new SerializationEntrySurrogate { Name = "Message", Value = "message", ObjectType = typeof(string) });
        entries.WriteField(ref writer, 0, typeof(SerializationEntrySurrogate),
            new SerializationEntrySurrogate { Name = "HResult", Value = -123, ObjectType = typeof(int) });
        ReferenceCodec.MarkValueField(session);
        writer.WriteStartObject(0, typeof(SerializationEntrySurrogate), typeof(SerializationEntrySurrogate));
        StringCodec.WriteField(ref writer, 0, "InnerException");
        WriteException(ref writer, 1, RuntimeTypeNameFormatter.Format(typeof(AdmittedException)));
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.Commit();
        return output.WrittenSpan.ToArray();
    }

    private static byte[] CreatePayload(ServiceProvider services, string? name, string? innerTypeName = null,
        bool includeData = false, bool omitName = false, bool repeatName = false, string? dataTypeName = null)
    {
        var output = new ArrayBufferWriter<byte>();
        using var session = services.GetRequiredService<SerializerSessionPool>().GetSession();
        var writer = Writer.Create(output, session);
        WriteException(ref writer, 0, name, innerTypeName, includeData, omitName, repeatName, dataTypeName);
        writer.Commit();
        return output.WrittenSpan.ToArray();
    }

    private static uint WriteException(ref Writer<ArrayBufferWriter<byte>> writer, uint delta, string? name,
        string? innerTypeName = null, bool includeData = false, bool omitName = false, bool repeatName = false,
        string? dataTypeName = null)
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
        uint dataReference = 0;
        if (dataTypeName is not null)
        {
            dataReference = ReferenceCodec.CreateRecordPlaceholder(writer.Session);
            writer.WriteStartObject(1, typeof(Dictionary<object, object?>), typeof(Dictionary<object, object?>));
            UInt32Codec.WriteField(ref writer, 1, 1);
            ((IFieldCodec<string>)new StringCodec()).WriteField(ref writer, 1, typeof(object), "key");
            WriteException(ref writer, 0, dataTypeName);
            writer.WriteEndObject();
        }
        else if (includeData)
        {
            var codec = writer.Session.CodecProvider.GetCodec<Dictionary<object, object?>>();
            codec.WriteField(ref writer, 1, typeof(Dictionary<object, object?>), new Dictionary<object, object?> { ["key"] = "value" });
        }

        writer.WriteEndObject();
        return dataReference;
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
    private abstract class AbstractException : Exception;
    private sealed class GenericException<T> : Exception;

    private sealed class DenyingExceptionNameFilter : ITypeNameFilter
    {
        public bool? IsTypeNameAllowed(string typeName, string assemblyName) =>
            typeName == typeof(AdmittedException).FullName ? false : null;
    }
}
