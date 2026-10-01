using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization.Activators;
using Orleans.Serialization.Serializers;
using Xunit;

namespace Orleans.Serialization.UnitTests;

[Trait("Category", "BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Serialization")]
public sealed class DefaultActivatorTests
{
    [Fact]
    public void ReferenceType_PublicParameterlessConstructor_InitializesEachInstance()
    {
        var activator = new DefaultReferenceTypeActivator<PublicConstructor>();

        var first = activator.Create();
        var second = activator.Create();

        Assert.Equal(42, first.Value);
        Assert.Equal(42, second.Value);
        Assert.NotSame(first, second);
        Assert.NotSame(first.State, second.State);
    }

    [Fact]
    public void ValueType_ExplicitParameterlessConstructor_InitializesEachInstance()
    {
        var activator = new DefaultValueTypeActivator<ExplicitValueConstructor>();

        var first = activator.Create();
        var second = activator.Create();

        Assert.Equal(42, first.Value);
        Assert.Equal(42, second.Value);
        Assert.NotNull(first.State);
        Assert.NotNull(second.State);
        Assert.NotSame(first.State, second.State);
    }

    [Fact]
    public void ReferenceType_PrivateParameterlessConstructor_AllocatesUninitializedInstance()
    {
        var activator = new DefaultReferenceTypeActivator<PrivateConstructor>();

        var first = activator.Create();
        var second = activator.Create();

        Assert.Equal(0, first.Value);
        Assert.Equal(0, second.Value);
        Assert.NotSame(first, second);
    }

    [Fact]
    public void ReferenceType_ParameterizedConstructor_AllocatesUninitializedInstance()
    {
        var instance = new DefaultReferenceTypeActivator<ParameterizedConstructor>().Create();

        Assert.Equal(0, instance.Value);
        Assert.Null(instance.State);
    }

    [Fact]
    public void ValueType_ImplicitParameterlessConstructor_AllocatesZeroInitializedValue()
    {
        var instance = new DefaultValueTypeActivator<ImplicitValueConstructor>().Create();

        Assert.Equal(0, instance.Value);
        Assert.Null(instance.State);
    }

    [Fact]
    public void ReferenceType_ThrowingConstructor_PropagatesOriginalException()
    {
        var activator = new DefaultReferenceTypeActivator<ThrowingReferenceConstructor>();

        var exception = Assert.Throws<InvalidOperationException>(() => activator.Create());

        Assert.Same(ThrowingReferenceConstructor.Error, exception);
        Assert.Contains(nameof(ThrowingReferenceConstructor), exception.StackTrace);
    }

    [Fact]
    public void ValueType_ThrowingConstructor_PropagatesOriginalException()
    {
        var activator = new DefaultValueTypeActivator<ThrowingValueConstructor>();

        var exception = Assert.Throws<InvalidOperationException>(() => activator.Create());

        Assert.Same(ThrowingValueConstructor.Error, exception);
        Assert.Contains(nameof(ThrowingValueConstructor), exception.StackTrace);
    }

    [Fact]
    public void ReferenceType_ConstructorThrowsTargetInvocationException_PropagatesConstructorException()
    {
        var activator = new DefaultReferenceTypeActivator<ThrowingInvocationReferenceConstructor>();

        var exception = Assert.Throws<TargetInvocationException>(() => activator.Create());

        Assert.Same(ThrowingInvocationReferenceConstructor.Error, exception);
        Assert.Same(ThrowingInvocationReferenceConstructor.Error.InnerException, exception.InnerException);
        Assert.Contains(nameof(ThrowingInvocationReferenceConstructor), exception.StackTrace);
    }

    [Fact]
    public void ValueType_ConstructorThrowsTargetInvocationException_PropagatesConstructorException()
    {
        var activator = new DefaultValueTypeActivator<ThrowingInvocationValueConstructor>();

        var exception = Assert.Throws<TargetInvocationException>(() => activator.Create());

        Assert.Same(ThrowingInvocationValueConstructor.Error, exception);
        Assert.Null(exception.InnerException);
        Assert.Contains(nameof(ThrowingInvocationValueConstructor), exception.StackTrace);
    }

    [Fact]
    public void CodecProvider_DefaultActivators_PreserveConstructorSelection()
    {
        using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var provider = services.GetRequiredService<CodecProvider>();

        Assert.Equal(42, provider.GetActivator<PublicConstructor>().Create().Value);
        Assert.Equal(42, provider.GetActivator<ExplicitValueConstructor>().Create().Value);
        Assert.Equal(0, provider.GetActivator<PrivateConstructor>().Create().Value);
        Assert.Equal(0, provider.GetActivator<ParameterizedConstructor>().Create().Value);
        Assert.Equal(0, provider.GetActivator<ImplicitValueConstructor>().Create().Value);
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

        [MethodImpl(MethodImplOptions.NoInlining)]
        public ThrowingReferenceConstructor() => throw Error;
    }

    private struct ThrowingValueConstructor
    {
        public static readonly InvalidOperationException Error = new("Value constructor failed.");

        [MethodImpl(MethodImplOptions.NoInlining)]
        public ThrowingValueConstructor() => throw Error;
    }

    private sealed class ThrowingInvocationReferenceConstructor
    {
        public static readonly TargetInvocationException Error = new(new InvalidOperationException("Reference constructor failed."));

        [MethodImpl(MethodImplOptions.NoInlining)]
        public ThrowingInvocationReferenceConstructor() => throw Error;
    }

    private struct ThrowingInvocationValueConstructor
    {
        public static readonly TargetInvocationException Error = new(null);

        [MethodImpl(MethodImplOptions.NoInlining)]
        public ThrowingInvocationValueConstructor() => throw Error;
    }
}
