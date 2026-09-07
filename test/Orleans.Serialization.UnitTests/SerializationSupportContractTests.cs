using System;
using System.Buffers;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.GeneratedCodeHelpers;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.Session;
using Orleans.Serialization.TypeSystem;
using Orleans.Serialization.Utilities;
using Orleans.Serialization.Utilities.Internal;
using Orleans.Serialization.WireProtocol;
using Xunit;

namespace Orleans.Serialization.UnitTests;

[Trait("Category", "BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Serialization")]
public sealed class SerializationSupportContractTests
{
    [Fact]
    public void GeneratedCodeHelper_NullCodecProvider_ThrowsWithExactParamName()
    {
        var exception = Assert.Throws<ArgumentNullException>(
            () => OrleansGeneratedCodeHelper.GetService<object>(this, null!));

        Assert.Equal("codecProvider", exception.ParamName);
    }

    [Fact]
    public void GeneratedCodeHelper_NullUnexpectedValue_ThrowsWithExactParamName()
    {
        var exception = Assert.Throws<ArgumentNullException>(SerializeNullUnexpectedValue);

        Assert.Equal("value", exception.ParamName);
    }

    [Fact]
    public void DotNetSerializableCodec_NullPayload_WritesAndReadsNullReference()
    {
        using var serviceProvider = CreateServiceProvider();
        var sessionPool = serviceProvider.GetRequiredService<SerializerSessionPool>();
        var codec = new DotNetSerializableCodec(serviceProvider.GetRequiredService<TypeConverter>());
        var output = new ArrayBufferWriter<byte>();

        using (var writerSession = sessionPool.GetSession())
        {
            var writer = Writer.Create(output, writerSession);
            codec.WriteField(ref writer, 0, typeof(object), null);
            writer.Commit();
        }

        using var readerSession = sessionPool.GetSession();
        var reader = Reader.Create(output.WrittenMemory, readerSession);
        var field = reader.ReadFieldHeader();
        var result = codec.ReadValue(ref reader, field);

        Assert.Equal(WireType.Reference, field.WireType);
        Assert.Null(result);
    }

    [Fact]
    public void ExceptionCodec_NullOptions_ThrowsWithExactParamName()
    {
        var exception = Assert.Throws<ArgumentNullException>(
            () => new ExceptionCodec(null!, null!, null!, null!, null!));

        Assert.Equal("exceptionSerializationOptions", exception.ParamName);
    }

    [Fact]
    public void ExceptionCodec_NullObjectDataValue_ThrowsWithExactParamName()
    {
        using var serviceProvider = CreateServiceProvider();
        var codec = serviceProvider.GetRequiredService<ExceptionCodec>();

        var exception = Assert.Throws<ArgumentNullException>(() => codec.GetObjectData(null!));

        Assert.Equal("value", exception.ParamName);
    }

    [Fact]
    public void ExceptionCodec_NullBasePropertiesValue_ThrowsWithExactParamName()
    {
        using var serviceProvider = CreateServiceProvider();
        var codec = serviceProvider.GetRequiredService<ExceptionCodec>();

        var exception = Assert.Throws<ArgumentNullException>(
            () => codec.SetBaseProperties(null!, null, null, null, 0, null));

        Assert.Equal("value", exception.ParamName);
    }

    [Fact]
    public void ExceptionCodec_NullDataException_ThrowsWithExactParamName()
    {
        using var serviceProvider = CreateServiceProvider();
        var codec = serviceProvider.GetRequiredService<ExceptionCodec>();

        var exception = Assert.Throws<ArgumentNullException>(() => codec.GetDataProperty(null!));

        Assert.Equal("exception", exception.ParamName);
    }

    [Fact]
    public void ExceptionCodec_RoundTrip_PreservesBaseExceptionState()
    {
        using var serviceProvider = CreateServiceProvider();
        var serializer = serviceProvider.GetRequiredService<Serializer>();
        var original = new InvalidOperationException("outer", new ArgumentException("inner"));
        original.Data["key"] = "value";

        var result = serializer.Deserialize<Exception>(serializer.SerializeToArray(original));

        var typedResult = Assert.IsType<InvalidOperationException>(result);
        Assert.Equal("outer", typedResult.Message);
        Assert.Equal("inner", Assert.IsType<ArgumentException>(typedResult.InnerException).Message);
        Assert.Equal("value", typedResult.Data["key"]);
    }

    [Fact]
    public void SerializationConstructorNotFoundException_NullType_ThrowsWithExactParamName()
    {
        var exception = Assert.Throws<ArgumentNullException>(
            () => new SerializationConstructorNotFoundException(null!));

        Assert.Equal("type", exception.ParamName);
    }

    [Fact]
    public void BitStreamFormatter_NullResult_ThrowsWithExactParamName()
    {
        var exception = Assert.Throws<ArgumentNullException>(FormatIntoNullResult);

        Assert.Equal("result", exception.ParamName);
    }

    [Theory]
    [InlineData(nameof(FieldAccessor.GetGetter))]
    [InlineData(nameof(FieldAccessor.GetValueGetter))]
    [InlineData(nameof(FieldAccessor.GetReferenceSetter))]
    [InlineData(nameof(FieldAccessor.GetValueSetter))]
    public void FieldAccessor_NullDeclaringType_ThrowsWithExactParamName(string methodName)
    {
        Action action = methodName switch
        {
            nameof(FieldAccessor.GetGetter) => () => FieldAccessor.GetGetter(null!, "_value"),
            nameof(FieldAccessor.GetValueGetter) => () => FieldAccessor.GetValueGetter(null!, "_value"),
            nameof(FieldAccessor.GetReferenceSetter) => () => FieldAccessor.GetReferenceSetter(null!, "_value"),
            nameof(FieldAccessor.GetValueSetter) => () => FieldAccessor.GetValueSetter(null!, "_value"),
            _ => throw new ArgumentOutOfRangeException(nameof(methodName)),
        };

        var exception = Assert.Throws<ArgumentNullException>(action);

        Assert.Equal("declaringType", exception.ParamName);
    }

    [Fact]
    public void FieldAccessor_ValidFields_PreserveReferenceAndValueTypeAccess()
    {
        var reference = new ReferenceHolder("original");
        var referenceGetter = (Func<ReferenceHolder, string>)FieldAccessor.GetGetter(typeof(ReferenceHolder), "_value");
        var referenceSetter = (Action<ReferenceHolder, string>)FieldAccessor.GetReferenceSetter(typeof(ReferenceHolder), "_value");
        var value = new ValueHolder(17);
        var valueGetter = (ValueTypeGetter<ValueHolder, int>)FieldAccessor.GetValueGetter(typeof(ValueHolder), "_value");
        var valueSetter = (ValueTypeSetter<ValueHolder, int>)FieldAccessor.GetValueSetter(typeof(ValueHolder), "_value");

        referenceSetter(reference, "updated");
        valueSetter(ref value, 29);

        Assert.Equal("updated", referenceGetter(reference));
        Assert.Equal(29, valueGetter(ref value));
    }

    [Fact]
    public void AddFromExisting_NullServices_ThrowsWithExactParamName()
    {
        var exception = Assert.Throws<ArgumentNullException>(
            () => InternalServiceCollectionExtensions.AddFromExisting(null!, typeof(IService), typeof(Service)));

        Assert.Equal("services", exception.ParamName);
    }

    [Fact]
    public void AddFromExisting_NullImplementation_ThrowsWithoutMutatingServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton<Service>();
        var originalCount = services.Count;

        var exception = Assert.Throws<ArgumentNullException>(
            () => InternalServiceCollectionExtensions.AddFromExisting(services, typeof(IService), null!));

        Assert.Equal("implementation", exception.ParamName);
        Assert.Equal(originalCount, services.Count);
    }

    [Fact]
    public void AddFromExisting_ValidRegistration_PreservesSingletonIdentity()
    {
        var services = new ServiceCollection();
        services.AddSingleton<Service>();
        services.AddFromExisting<IService, Service>();
        using var serviceProvider = services.BuildServiceProvider();

        Assert.Same(
            serviceProvider.GetRequiredService<Service>(),
            serviceProvider.GetRequiredService<IService>());
    }

    private static ServiceProvider CreateServiceProvider() =>
        new ServiceCollection()
            .AddSerializer()
            .BuildServiceProvider();

    private static void SerializeNullUnexpectedValue()
    {
        Writer<ArrayBufferWriter<byte>> writer = default;
        OrleansGeneratedCodeHelper.SerializeUnexpectedType(ref writer, 0, null, null!);
    }

    private static void FormatIntoNullResult()
    {
        Reader<byte[]> reader = default;
        BitStreamFormatter.Format(ref reader, null!);
    }

    private interface IService;

    private sealed class Service : IService;

    private sealed class ReferenceHolder(string value)
    {
        private string _value = value;
    }

    private struct ValueHolder(int value)
    {
        private int _value = value;
    }
}
