using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Orleans.Serialization.Activators;

namespace Orleans.Serialization.ActivationSmoke;

internal static class Program
{
    private static void Main()
    {
#if NATIVE_AOT_SMOKE
        Ensure(!RuntimeFeature.IsDynamicCodeSupported, "NativeAOT smoke requires dynamic code to be disabled.");
#endif
        ValidatePublicReferenceConstructor();
        ValidateExplicitValueConstructor();
        ValidateUninitializedAllocation();
        ValidateConstructorExceptions();
        Console.WriteLine($"Default activator smoke passed. Dynamic code supported: {RuntimeFeature.IsDynamicCodeSupported}.");
    }

    private static void ValidatePublicReferenceConstructor()
    {
        var activator = new DefaultReferenceTypeActivator<PublicConstructor>();
        var first = activator.Create();
        var second = activator.Create();

        Ensure(first.Value == 42 && second.Value == 42, "Public reference constructor did not initialize each instance.");
        Ensure(!ReferenceEquals(first, second), "Reference activation reused an instance.");
        Ensure(!ReferenceEquals(first.State, second.State), "Reference constructor state was reused.");
    }

    private static void ValidateExplicitValueConstructor()
    {
        var activator = new DefaultValueTypeActivator<ExplicitValueConstructor>();
        var first = activator.Create();
        var second = activator.Create();

        Ensure(first.Value == 42 && second.Value == 42, "Explicit value constructor did not initialize each instance.");
        Ensure(first.State is not null && second.State is not null, "Explicit value constructor state was not initialized.");
        Ensure(!ReferenceEquals(first.State, second.State), "Value constructor state was reused.");
    }

    private static void ValidateUninitializedAllocation()
    {
        var privateActivator = new DefaultReferenceTypeActivator<PrivateConstructor>();
        var first = privateActivator.Create();
        var second = privateActivator.Create();
        Ensure(first.Value == 0 && second.Value == 0, "Private constructor field initialization was executed.");
        Ensure(!ReferenceEquals(first, second), "Uninitialized reference activation reused an instance.");

        var reference = new DefaultReferenceTypeActivator<ParameterizedConstructor>().Create();
        Ensure(reference.Value == 0 && reference.State is null, "Parameterized reference constructor initialization was executed.");

        var value = new DefaultValueTypeActivator<ImplicitValueConstructor>().Create();
        Ensure(value.Value == 0 && value.State is null, "Implicit value activation did not produce a zero-initialized value.");
    }

    private static void ValidateConstructorExceptions()
    {
        var referenceActivator = new DefaultReferenceTypeActivator<ThrowingReferenceConstructor>();
        EnsureOriginalException(() => referenceActivator.Create(), ThrowingReferenceConstructor.Error);

        var valueActivator = new DefaultValueTypeActivator<ThrowingValueConstructor>();
        EnsureOriginalException(() => valueActivator.Create(), ThrowingValueConstructor.Error);
    }

    private static void EnsureOriginalException(Action create, InvalidOperationException expected)
    {
        try
        {
            create();
        }
        catch (InvalidOperationException exception) when (ReferenceEquals(exception, expected))
        {
            return;
        }

        throw new InvalidOperationException("Activation did not propagate the original constructor exception.");
    }

    private static void Ensure([DoesNotReturnIf(false)] bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class PublicConstructor
    {
        public PublicConstructor()
        {
            Value = 42;
            State = new object();
        }

        public int Value { get; }
        public object State { get; }
    }

    private struct ExplicitValueConstructor
    {
        public ExplicitValueConstructor()
        {
            Value = 42;
            State = new object();
        }

        public int Value { get; }
        public object State { get; }
    }

    private sealed class PrivateConstructor
    {
        private PrivateConstructor() => throw new InvalidOperationException("Private constructor invoked.");

        public int Value { get; } = 42;
    }

    private sealed class ParameterizedConstructor(int value)
    {
        public int Value { get; } = value;
        public object State { get; } = new();
    }

    private struct ImplicitValueConstructor(int value)
    {
        public int Value { get; } = value;
        public object State { get; } = new();
    }

    private sealed class ThrowingReferenceConstructor
    {
        public static readonly InvalidOperationException Error = new("Reference constructor failed.");

        public ThrowingReferenceConstructor() => throw Error;
    }

    private struct ThrowingValueConstructor
    {
        public static readonly InvalidOperationException Error = new("Value constructor failed.");

        public ThrowingValueConstructor() => throw Error;
    }
}
