using System;
using System.Collections.Generic;
using System.Reflection;
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
        ValidatePublicReferenceConstructor();
        ValidateValueConstructor(boxedFirst: true);
        ValidateValueConstructor(boxedFirst: false);
        ValidateConstructorExceptions(targetInvocationException: false);
        ValidateConstructorExceptions(targetInvocationException: true);
        ValidateExceptionFallback();
        ValidateExceptionCodec();
        ValidateMissingConstructor();
        Console.WriteLine("Native serialization constructors passed: existing-object identity and cycles, public/private constructors, ref/boxed structs, shape-isolated caches, original exception identity and constructor stacks, base Exception fallback, ordinary ExceptionCodec. Dynamic code: False.");
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
        Ensure(ReferenceEquals(value.Info, info) && value.ContextState == StreamingContextStates.All, "The constructor must receive the original serialization info and context state.");
        Ensure(ReferenceEquals(constructor, factory.GetSerializationConstructorDelegate(typeof(ReferenceValue))), "Reference constructor delegates must be cached.");
    }

    private static void ValidatePublicReferenceConstructor()
    {
        var constructor = new SerializationConstructorFactory().GetSerializationConstructorDelegate(typeof(PublicReferenceValue));
        var value = (PublicReferenceValue)RuntimeHelpers.GetUninitializedObject(typeof(PublicReferenceValue));
        var alias = value;
        var info = CreateInfo(typeof(PublicReferenceValue));
        var context = new object();

        constructor(value, info, new StreamingContext(StreamingContextStates.All, context));

        Ensure(ReferenceEquals(alias, value) && ReferenceEquals(value.Info, info), "The public constructor must initialize the existing object with the original serialization info.");
        Ensure(ReferenceEquals(value.Context.Context, context) && value.Context.State == StreamingContextStates.All, "The public constructor must receive the original streaming context.");
    }

    private static void ValidateValueConstructor(bool boxedFirst)
    {
        var factory = new SerializationConstructorFactory();
        Action<object, SerializationInfo, StreamingContext> boxedConstructor;
        ValueTypeSerializer<StructValue>.ValueConstructor constructor;
        if (boxedFirst)
        {
            boxedConstructor = factory.GetSerializationConstructorDelegate(typeof(StructValue));
            constructor = factory.GetSerializationConstructorDelegate<StructValue>();
        }
        else
        {
            constructor = factory.GetSerializationConstructorDelegate<StructValue>();
            boxedConstructor = factory.GetSerializationConstructorDelegate(typeof(StructValue));
        }

        var context = new object();
        var info = CreateInfo(typeof(StructValue));
        info.AddValue("Payload", 73);
        StructValue value = default;

        constructor(ref value, info, new StreamingContext(StreamingContextStates.All, context));

        Ensure(value.Payload == 73 && ReferenceEquals(value.Context, context), "The private struct constructor must update the caller's ref value.");
        Ensure(ReferenceEquals(value.Info, info) && value.ContextState == StreamingContextStates.All, "The ref constructor must receive the original serialization info and context state.");
        Ensure(ReferenceEquals(constructor, factory.GetSerializationConstructorDelegate<StructValue>()), "Struct constructor delegates must be cached.");

        object boxed = default(StructValue);
        var alias = boxed;
        boxedConstructor(boxed, info, new StreamingContext(StreamingContextStates.All, context));
        Ensure(ReferenceEquals(alias, boxed) && ((StructValue)alias).Payload == 73, "The object constructor delegate must mutate the existing box.");
        Ensure(ReferenceEquals(((StructValue)alias).Context, context), "The boxed constructor must receive the streaming context.");
        Ensure(ReferenceEquals(boxedConstructor, factory.GetSerializationConstructorDelegate(typeof(StructValue))), "Boxed constructor delegates must be cached independently of ref delegates.");
    }

    private static void ValidateConstructorExceptions(bool targetInvocationException)
    {
        var factory = new SerializationConstructorFactory();
        var referenceConstructor = factory.GetSerializationConstructorDelegate(typeof(ThrowingReference));
        var reference = (ThrowingReference)RuntimeHelpers.GetUninitializedObject(typeof(ThrowingReference));
        var referenceFailure = CreateFailure(targetInvocationException);
        try
        {
            referenceConstructor(reference, CreateInfo(typeof(ThrowingReference)), new StreamingContext(StreamingContextStates.All, referenceFailure));
            throw new InvalidOperationException("The reference constructor must throw.");
        }
        catch (Exception exception) when (ReferenceEquals(exception, referenceFailure))
        {
            Ensure(reference.Payload == 17, "Reference mutations before a constructor exception must remain visible.");
            Ensure(exception.StackTrace!.Contains(nameof(ThrowingReference), StringComparison.Ordinal), "The reference constructor must remain in the original exception stack.");
        }

        var valueConstructor = factory.GetSerializationConstructorDelegate<ThrowingStruct>();
        ThrowingStruct value = default;
        var valueFailure = CreateFailure(targetInvocationException);
        try
        {
            valueConstructor(ref value, CreateInfo(typeof(ThrowingStruct)), new StreamingContext(StreamingContextStates.All, valueFailure));
            throw new InvalidOperationException("The struct constructor must throw.");
        }
        catch (Exception exception) when (ReferenceEquals(exception, valueFailure))
        {
            Ensure(value.Payload == 29, "Struct mutations before a constructor exception must remain visible.");
            Ensure(exception.StackTrace!.Contains(nameof(ThrowingStruct), StringComparison.Ordinal), "The ref constructor must remain in the original exception stack.");
        }

        var boxedConstructor = factory.GetSerializationConstructorDelegate(typeof(ThrowingStruct));
        object boxed = default(ThrowingStruct);
        var alias = boxed;
        var boxedFailure = CreateFailure(targetInvocationException);
        try
        {
            boxedConstructor(boxed, CreateInfo(typeof(ThrowingStruct)), new StreamingContext(StreamingContextStates.All, boxedFailure));
            throw new InvalidOperationException("The boxed struct constructor must throw.");
        }
        catch (Exception exception) when (ReferenceEquals(exception, boxedFailure))
        {
            Ensure(ReferenceEquals(alias, boxed) && ((ThrowingStruct)alias).Payload == 29, "Boxed struct mutations before a constructor exception must remain visible on the existing box.");
            Ensure(exception.StackTrace!.Contains(nameof(ThrowingStruct), StringComparison.Ordinal), "The boxed constructor must remain in the original exception stack.");
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

    private static Exception CreateFailure(bool targetInvocationException)
    {
        var failure = new InvalidOperationException("constructor");
        return targetInvocationException ? new TargetInvocationException("user exception", failure) : failure;
    }

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
        public SerializationInfo? Info;
        public StreamingContextStates ContextState;

        private ReferenceValue(SerializationInfo info, StreamingContext context)
        {
            Payload = info.GetInt32("Payload");
            Self = (ReferenceValue?)info.GetValue("Self", typeof(ReferenceValue));
            Context = context.Context;
            Info = info;
            ContextState = context.State;
        }
    }

    private struct StructValue
    {
        public int Payload;
        public object? Context;
        public SerializationInfo? Info;
        public StreamingContextStates ContextState;

        private StructValue(SerializationInfo info, StreamingContext context)
        {
            Payload = info.GetInt32("Payload");
            Context = context.Context;
            Info = info;
            ContextState = context.State;
        }
    }

    private sealed class PublicReferenceValue
    {
        public SerializationInfo Info;
        public StreamingContext Context;

        public PublicReferenceValue(SerializationInfo info, StreamingContext context)
        {
            Info = info;
            Context = context;
        }
    }

    private sealed class ThrowingReference
    {
        public int Payload;

        [MethodImpl(MethodImplOptions.NoInlining)]
        private ThrowingReference(SerializationInfo info, StreamingContext context)
        {
            Payload = 17;
            throw (Exception)context.Context!;
        }
    }

    private struct ThrowingStruct
    {
        public int Payload;

        [MethodImpl(MethodImplOptions.NoInlining)]
        private ThrowingStruct(SerializationInfo info, StreamingContext context)
        {
            Payload = 29;
            throw (Exception)context.Context!;
        }
    }

    private sealed class NonConformingException : Exception;
}
