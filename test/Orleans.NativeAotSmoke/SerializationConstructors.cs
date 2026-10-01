using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using Microsoft.Extensions.Options;
using Orleans.Serialization;

namespace Orleans.NativeAotSmoke;

#pragma warning disable SYSLIB0050, SYSLIB0051 // Exercise the legacy serialization constructor contract.

internal static class SerializationConstructors
{
    private static void Main()
    {
        ValidateReferenceConstructor();
        ValidateValueConstructor();
        ValidateConstructorExceptions();
        ValidateExceptionFallback();
        ValidateExceptionCodec();
        ValidateMissingConstructor();
        Console.WriteLine("Native serialization constructors passed: existing-object identity and cycles, private constructors, ref/boxed structs, original exceptions, base Exception fallback, ordinary ExceptionCodec. Dynamic code: False.");
    }

    private static void ValidateReferenceConstructor()
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

        Ensure(ReferenceEquals(alias, value) && ReferenceEquals(value, value.Self), "Reference identity and the self-cycle must survive constructor initialization.");
        Ensure(value.Payload == 42 && ReferenceEquals(value.Context, context), "The private constructor must restore fields and receive the streaming context.");
        Ensure(ReferenceEquals(constructor, factory.GetSerializationConstructorDelegate(typeof(ReferenceValue))), "Reference constructor delegates must be cached.");
    }

    private static void ValidateValueConstructor()
    {
        var factory = new SerializationConstructorFactory();
        var constructor = factory.GetSerializationConstructorDelegate<StructValue>();
        var context = new object();
        var info = CreateInfo(typeof(StructValue));
        info.AddValue("Payload", 73);
        StructValue value = default;

        constructor(ref value, info, new StreamingContext(StreamingContextStates.All, context));

        Ensure(value.Payload == 73 && ReferenceEquals(value.Context, context), "The private struct constructor must update the caller's ref value.");
        Ensure(ReferenceEquals(constructor, factory.GetSerializationConstructorDelegate<StructValue>()), "Struct constructor delegates must be cached.");

        var boxedConstructor = new SerializationConstructorFactory().GetSerializationConstructorDelegate(typeof(StructValue));
        object boxed = default(StructValue);
        var alias = boxed;
        boxedConstructor(boxed, info, default);
        Ensure(ReferenceEquals(alias, boxed) && ((StructValue)alias).Payload == 73, "The object constructor delegate must mutate the existing box.");
    }

    private static void ValidateConstructorExceptions()
    {
        var factory = new SerializationConstructorFactory();
        var referenceConstructor = factory.GetSerializationConstructorDelegate(typeof(ThrowingReference));
        var reference = (ThrowingReference)RuntimeHelpers.GetUninitializedObject(typeof(ThrowingReference));
        var referenceFailure = new InvalidOperationException("reference constructor");
        try
        {
            referenceConstructor(reference, CreateInfo(typeof(ThrowingReference)), new StreamingContext(StreamingContextStates.All, referenceFailure));
            throw new InvalidOperationException("The reference constructor must throw.");
        }
        catch (InvalidOperationException exception) when (ReferenceEquals(exception, referenceFailure))
        {
            Ensure(reference.Payload == 17, "Reference mutations before a constructor exception must remain visible.");
        }

        var valueConstructor = factory.GetSerializationConstructorDelegate<ThrowingStruct>();
        ThrowingStruct value = default;
        var valueFailure = new InvalidOperationException("struct constructor");
        try
        {
            valueConstructor(ref value, CreateInfo(typeof(ThrowingStruct)), new StreamingContext(StreamingContextStates.All, valueFailure));
            throw new InvalidOperationException("The struct constructor must throw.");
        }
        catch (InvalidOperationException exception) when (ReferenceEquals(exception, valueFailure))
        {
            Ensure(value.Payload == 29, "Struct mutations before a constructor exception must remain visible.");
        }
    }

    private static void ValidateExceptionFallback()
    {
        var factory = new SerializationConstructorFactory();
        Ensure(!SerializationConstructorFactory.HasSerializationConstructor(typeof(NonConformingException)), "The fallback fixture must have no serialization constructor.");
        var constructor = factory.GetSerializationConstructorDelegate(typeof(NonConformingException));
        var value = (NonConformingException)RuntimeHelpers.GetUninitializedObject(typeof(NonConformingException));
        var original = new Exception("fallback", new ArgumentException("inner"));
        original.Data["key"] = "value";
        var info = CreateInfo(typeof(Exception));
        original.GetObjectData(info, default);

        constructor(value, info, default);

        Ensure(value.GetType() == typeof(NonConformingException) && value.Message == "fallback", "The base Exception constructor must initialize the existing subtype.");
        Ensure(ReferenceEquals(value.InnerException, original.InnerException) && value.HResult == original.HResult, "Base exception fields must be restored.");
        Ensure(Equals(value.Data["key"], "value"), "Base exception data must be restored.");
    }

    private static void ValidateExceptionCodec()
    {
        var codec = new ExceptionCodec(null!, null!, null!, null!, Options.Create(new ExceptionSerializationOptions()));
        var value = (NonConformingException)RuntimeHelpers.GetUninitializedObject(typeof(NonConformingException));
        var alias = value;
        var inner = new Exception("inner");

        codec.SetBaseProperties(value, "outer", null, inner, 123, new Dictionary<object, object?> { ["key"] = "value" });

        Ensure(ReferenceEquals(value, alias) && value.Message == "outer", "Ordinary ExceptionCodec construction must support existing-object initialization.");
        Ensure(ReferenceEquals(value.InnerException, inner) && value.HResult == 123 && Equals(value.Data["key"], "value"), "ExceptionCodec must restore base state.");
    }

    private static void ValidateMissingConstructor()
    {
        try
        {
            new SerializationConstructorFactory().GetSerializationConstructorDelegate(typeof(object));
            throw new InvalidOperationException("Missing serialization constructors must throw.");
        }
        catch (SerializationException exception)
        {
            Ensure(exception.Message.Contains("ISerializable constructor not found", StringComparison.Ordinal), "Missing-constructor errors must identify the serialization contract.");
        }
    }

    private static SerializationInfo CreateInfo(Type type) => new(type, new FormatterConverter());

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

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
}
