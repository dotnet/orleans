using System;
using System.Reflection;
using System.Runtime.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Orleans.Serialization.UnitTests;

[Trait("Category", "BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Serialization")]
public sealed class ReferenceTypeExceptionTests
{
    [Theory]
    [InlineData(false, 0U)]
    [InlineData(false, 17U)]
    [InlineData(false, uint.MaxValue)]
    [InlineData(true, 0U)]
    [InlineData(true, 17U)]
    [InlineData(true, uint.MaxValue)]
    public void LegacyMetadata_RoundTripsTypeIdentityWithoutMutatingSource(bool wellKnown, uint id)
    {
        var original = Create(wellKnown, id);
        var propertyName = wellKnown ? nameof(UnknownWellKnownTypeException.Id) : nameof(UnknownReferencedTypeException.Reference);
        var info = GetMetadata(original);

        Assert.Equal(id, info.GetUInt32(propertyName));
        Assert.Equal(id, GetId(original));
        var constructor = original.GetType().GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic, null,
            [typeof(SerializationInfo), typeof(StreamingContext)], null)!;
        var result = Assert.IsAssignableFrom<SerializerException>(constructor.Invoke([info, default(StreamingContext)]));

        Assert.Equal(original.GetType(), result.GetType());
        Assert.Equal(id, GetId(result));
        Assert.Equal(original.Message, result.Message);
    }

    [Theory]
    [InlineData(false, 0U)]
    [InlineData(false, 17U)]
    [InlineData(false, uint.MaxValue)]
    [InlineData(true, 0U)]
    [InlineData(true, 17U)]
    [InlineData(true, uint.MaxValue)]
    public void OrleansSerialization_RoundTripsTypeIdentity(bool wellKnown, uint id)
    {
        using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = services.GetRequiredService<Serializer<SerializerException>>();
        var original = Create(wellKnown, id);

        var result = Assert.IsAssignableFrom<SerializerException>(serializer.Deserialize(serializer.SerializeToArray(original)));

        Assert.Equal(original.GetType(), result.GetType());
        Assert.Equal(id, GetId(result));
        Assert.Equal(original.Message, result.Message);
        Assert.Equal(id, GetId(original));
    }

    private static SerializerException Create(bool wellKnown, uint id)
        => wellKnown ? new UnknownWellKnownTypeException(id) : new UnknownReferencedTypeException(id);

    private static uint GetId(SerializerException exception) => exception switch
    {
        UnknownWellKnownTypeException value => value.Id,
        UnknownReferencedTypeException value => value.Reference,
        _ => throw new InvalidOperationException($"Unexpected exception type {exception.GetType()}."),
    };

#pragma warning disable SYSLIB0050, SYSLIB0051 // The compatibility regression exercises the existing SerializationInfo contract.
    private static SerializationInfo GetMetadata(SerializerException exception)
    {
        var info = new SerializationInfo(exception.GetType(), new FormatterConverter());
        exception.GetObjectData(info, default);
        return info;
    }
#pragma warning restore SYSLIB0050, SYSLIB0051
}
