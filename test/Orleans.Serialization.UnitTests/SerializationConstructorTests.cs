using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Orleans.Serialization.UnitTests;

#pragma warning disable SYSLIB0050, SYSLIB0051 // Exercise the legacy serialization constructor contract.

[Trait("Category", "BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Serialization")]
public sealed class SerializationConstructorTests
{
    [Fact]
    public void ReferenceConstructor_InitializesExistingObjectAndPreservesCycle()
    {
        var factory = new SerializationConstructorFactory();
        var constructor = factory.GetSerializationConstructorDelegate(typeof(ReferenceValue));
        var value = (ReferenceValue)RuntimeHelpers.GetUninitializedObject(typeof(ReferenceValue));
        var alias = value;
        var context = new object();
        var info = CreateInfo(typeof(ReferenceValue));
        info.AddValue("Payload", 42);
        info.AddValue("Self", value, typeof(ReferenceValue));

        constructor(value, info, new StreamingContext(StreamingContextStates.All, context));

        Assert.Same(alias, value);
        Assert.Same(value, value.Self);
        Assert.Same(context, value.Context);
        Assert.Equal(42, value.Payload);
        Assert.Same(constructor, factory.GetSerializationConstructorDelegate(typeof(ReferenceValue)));
    }

    [Fact]
    public void ValueConstructor_InitializesOriginalRefAndPreservesContext()
    {
        var factory = new SerializationConstructorFactory();
        var constructor = factory.GetSerializationConstructorDelegate<StructValue>();
        var context = new object();
        var info = CreateInfo(typeof(StructValue));
        info.AddValue("Payload", 73);
        StructValue value = default;

        constructor(ref value, info, new StreamingContext(StreamingContextStates.All, context));

        Assert.Equal(73, value.Payload);
        Assert.Same(context, value.Context);
        Assert.Same(constructor, factory.GetSerializationConstructorDelegate<StructValue>());
    }

    [Fact]
    public void BoxedValueConstructor_MutatesExistingBox()
    {
        var constructor = new SerializationConstructorFactory().GetSerializationConstructorDelegate(typeof(StructValue));
        object value = default(StructValue);
        var alias = value;
        var info = CreateInfo(typeof(StructValue));
        info.AddValue("Payload", 91);

        constructor(value, info, default);

        Assert.Same(alias, value);
        Assert.Equal(91, ((StructValue)alias).Payload);
    }

    [Fact]
    public void StructConstructorCache_BoxedThenRef_PreservesBothDelegates()
        => ValidateStructConstructorCache(boxedFirst: true);

    [Fact]
    public void StructConstructorCache_RefThenBoxed_PreservesBothDelegates()
        => ValidateStructConstructorCache(boxedFirst: false);

    private static void ValidateStructConstructorCache(bool boxedFirst)
    {
        var factory = new SerializationConstructorFactory();
        Action<object, SerializationInfo, StreamingContext> boxedConstructor;
        ValueTypeSerializer<StructValue>.ValueConstructor refConstructor;
        if (boxedFirst)
        {
            boxedConstructor = factory.GetSerializationConstructorDelegate(typeof(StructValue));
            refConstructor = factory.GetSerializationConstructorDelegate<StructValue>();
        }
        else
        {
            refConstructor = factory.GetSerializationConstructorDelegate<StructValue>();
            boxedConstructor = factory.GetSerializationConstructorDelegate(typeof(StructValue));
        }

        var context = new object();
        var info = CreateInfo(typeof(StructValue));
        info.AddValue("Payload", 83);
        var streamingContext = new StreamingContext(StreamingContextStates.All, context);
        object boxed = default(StructValue);
        var alias = boxed;
        StructValue value = default;

        boxedConstructor(boxed, info, streamingContext);
        refConstructor(ref value, info, streamingContext);

        Assert.Same(alias, boxed);
        Assert.Equal(83, ((StructValue)alias).Payload);
        Assert.Same(context, ((StructValue)alias).Context);
        Assert.Equal(83, value.Payload);
        Assert.Same(context, value.Context);
        Assert.Same(boxedConstructor, factory.GetSerializationConstructorDelegate(typeof(StructValue)));
        Assert.Same(refConstructor, factory.GetSerializationConstructorDelegate<StructValue>());
    }

    [Fact]
    public void ReferenceConstructor_PropagatesOriginalExceptionAndPartialMutation()
    {
        var constructor = new SerializationConstructorFactory().GetSerializationConstructorDelegate(typeof(ThrowingReference));
        var value = (ThrowingReference)RuntimeHelpers.GetUninitializedObject(typeof(ThrowingReference));
        var failure = new InvalidOperationException("reference constructor");

        var thrown = Assert.Throws<InvalidOperationException>(
            () => constructor(value, CreateInfo(typeof(ThrowingReference)), new StreamingContext(StreamingContextStates.All, failure)));

        Assert.Same(failure, thrown);
        Assert.Equal(17, value.Payload);
    }

    [Fact]
    public void ValueConstructor_PropagatesOriginalExceptionAndPartialMutation()
    {
        var constructor = new SerializationConstructorFactory().GetSerializationConstructorDelegate<ThrowingStruct>();
        ThrowingStruct value = default;
        var failure = new InvalidOperationException("struct constructor");

        var thrown = Assert.Throws<InvalidOperationException>(
            () => constructor(ref value, CreateInfo(typeof(ThrowingStruct)), new StreamingContext(StreamingContextStates.All, failure)));

        Assert.Same(failure, thrown);
        Assert.Equal(29, value.Payload);
    }

    [Fact]
    public void ExceptionWithoutSerializationConstructor_InitializesBaseOnExistingSubtype()
    {
        var factory = new SerializationConstructorFactory();
        Assert.False(SerializationConstructorFactory.HasSerializationConstructor(typeof(NonConformingException)));
        var constructor = factory.GetSerializationConstructorDelegate(typeof(NonConformingException));
        var value = (NonConformingException)RuntimeHelpers.GetUninitializedObject(typeof(NonConformingException));
        var original = new Exception("fallback", new ArgumentException("inner"));
        original.Data["key"] = "value";
        var info = CreateInfo(typeof(Exception));
        original.GetObjectData(info, default);

        constructor(value, info, default);

        Assert.IsType<NonConformingException>(value);
        Assert.Equal("fallback", value.Message);
        Assert.Same(original.InnerException, value.InnerException);
        Assert.Equal(original.HResult, value.HResult);
        Assert.Equal("value", value.Data["key"]);
    }

    [Fact]
    public void MissingSerializationConstructor_ThrowsSerializationException()
    {
        Assert.False(SerializationConstructorFactory.HasSerializationConstructor(typeof(object)));
        var exception = Assert.Throws<SerializationException>(
            () => new SerializationConstructorFactory().GetSerializationConstructorDelegate(typeof(object)));

        Assert.Contains("ISerializable constructor not found", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExceptionCodec_OrdinaryConstructionInitializesExistingException()
    {
        var codec = new ExceptionCodec(null!, null!, null!, null!, Options.Create(new ExceptionSerializationOptions()));
        var value = (NonConformingException)RuntimeHelpers.GetUninitializedObject(typeof(NonConformingException));
        var alias = value;
        var inner = new Exception("inner");

        codec.SetBaseProperties(value, "outer", null, inner, 123, new Dictionary<object, object?> { ["key"] = "value" });

        Assert.Same(alias, value);
        Assert.Equal("outer", value.Message);
        Assert.Same(inner, value.InnerException);
        Assert.Equal(123, value.HResult);
        Assert.Equal("value", value.Data["key"]);
    }

    [Fact]
    public void SerializableCodec_RoundTripPreservesCycleAndCallbackOrder()
    {
        using var services = new ServiceCollection()
            .AddSerializer()
            .AddSingleton<Serializers.IGeneralizedCodec, DotNetSerializableCodec>()
            .BuildServiceProvider();
        var serializer = services.GetRequiredService<Serializer<object>>();
        var original = new CallbackValue();

        var result = Assert.IsType<CallbackValue>(serializer.Deserialize(serializer.SerializeToArray(original)));

        Assert.NotSame(original, result);
        Assert.Same(result, result.Self);
        Assert.Equal(new[] { "deserializing", "constructor", "deserialized", "callback" }, result.History);
    }

    private static SerializationInfo CreateInfo(Type type) => new(type, new FormatterConverter());

    private sealed class ReferenceValue
    {
        public int Payload;
        public ReferenceValue? Self;
        public object? Context;

        private ReferenceValue(SerializationInfo info, StreamingContext context)
        {
            Payload = info.GetInt32("Payload");
            Self = (ReferenceValue?)info.GetValue("Self", typeof(ReferenceValue));
            Context = context.Context;
        }
    }

    private struct StructValue
    {
        public int Payload;
        public object? Context;

        private StructValue(SerializationInfo info, StreamingContext context)
        {
            Payload = info.GetInt32("Payload");
            Context = context.Context;
        }
    }

    private sealed class ThrowingReference
    {
        public int Payload;

        private ThrowingReference(SerializationInfo info, StreamingContext context)
        {
            Payload = 17;
            throw (InvalidOperationException)context.Context!;
        }
    }

    private struct ThrowingStruct
    {
        public int Payload;

        private ThrowingStruct(SerializationInfo info, StreamingContext context)
        {
            Payload = 29;
            throw (InvalidOperationException)context.Context!;
        }
    }

    private sealed class NonConformingException : Exception;

    [Serializable]
    private sealed class CallbackValue : ISerializable, IDeserializationCallback
    {
        public List<string>? History;
        public CallbackValue Self;

        public CallbackValue()
        {
            History = new();
            Self = this;
        }

        private CallbackValue(SerializationInfo info, StreamingContext context)
        {
            History!.Add("constructor");
            Self = (CallbackValue)info.GetValue("Self", typeof(CallbackValue))!;
        }

        [OnDeserializing]
        private void OnDeserializing(StreamingContext context)
        {
            History = new() { "deserializing" };
        }

        [OnDeserialized]
        private void OnDeserialized(StreamingContext context) => History!.Add("deserialized");

        public void OnDeserialization(object? sender) => History!.Add("callback");

        public void GetObjectData(SerializationInfo info, StreamingContext context) => info.AddValue("Self", Self);
    }
}
