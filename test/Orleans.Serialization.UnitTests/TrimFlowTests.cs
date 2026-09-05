using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Configuration;
using Orleans.Serialization.GeneratedCodeHelpers;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.Utilities;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Xunit;

namespace Orleans.Serialization.UnitTests;

[Trait("Category", "BVT")]
[Trait("Suite", "BVT")]
[Trait("Provider", "None")]
[Trait("Area", "Serialization")]
public class TrimFlowTests
{
    [Theory]
    [InlineData(nameof(TypeManifestOptions.Activators), nameof(TypeManifestOptions.AddActivator), DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.Interfaces)]
    [InlineData(nameof(TypeManifestOptions.FieldCodecs), nameof(TypeManifestOptions.AddFieldCodec), DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.Interfaces)]
    [InlineData(nameof(TypeManifestOptions.Serializers), nameof(TypeManifestOptions.AddSerializer), DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.Interfaces)]
    [InlineData(nameof(TypeManifestOptions.Copiers), nameof(TypeManifestOptions.AddCopier), DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.Interfaces)]
    [InlineData(nameof(TypeManifestOptions.Converters), nameof(TypeManifestOptions.AddConverter), DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.Interfaces)]
    [InlineData(nameof(TypeManifestOptions.Interfaces), nameof(TypeManifestOptions.AddInterface), DynamicallyAccessedMemberTypes.PublicMethods | DynamicallyAccessedMemberTypes.NonPublicMethods | DynamicallyAccessedMemberTypes.Interfaces)]
    [InlineData(nameof(TypeManifestOptions.InterfaceProxies), nameof(TypeManifestOptions.AddInterfaceProxy), DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.Interfaces)]
    [InlineData(nameof(TypeManifestOptions.InterfaceImplementations), nameof(TypeManifestOptions.AddInterfaceImplementation), DynamicallyAccessedMemberTypes.Interfaces)]
    public void TypeManifestRegistrations_ExposeTrimSafeAlternative(
        string legacyPropertyName,
        string registrationMethodName,
        DynamicallyAccessedMemberTypes expectedMembers)
    {
        var property = typeof(TypeManifestOptions).GetProperty(legacyPropertyName)!;
        var legacyWarning = property.GetMethod!.GetCustomAttribute<RequiresUnreferencedCodeAttribute>();
        var registrationParameter = typeof(TypeManifestOptions).GetMethod(registrationMethodName)!.GetParameters()[0];
        var preservedMembers = registrationParameter.GetCustomAttribute<DynamicallyAccessedMembersAttribute>();

        Assert.NotNull(legacyWarning);
        Assert.Contains(registrationMethodName, legacyWarning.Message, StringComparison.Ordinal);
        Assert.Equal(expectedMembers, preservedMembers?.MemberTypes);
    }

    [Fact]
    public void ShallowCopyableTypes_InspectsPrivateValueTypeFields()
    {
        Assert.False(ShallowCopyableTypes.Contains(typeof(StructWithPrivateReference)));
    }

    [Fact]
    public void FieldAccessor_AccessesPrivateFieldOnClosedGenericReferenceType()
    {
        var instance = new GenericReferenceHolder<string>("original");
        var getter = (Func<GenericReferenceHolder<string>, string>)FieldAccessor.GetGetter(
            typeof(GenericReferenceHolder<string>),
            "_value");
        var setter = (Action<GenericReferenceHolder<string>, string>)FieldAccessor.GetReferenceSetter(
            typeof(GenericReferenceHolder<string>),
            "_value");

        Assert.Equal("original", getter(instance));

        setter(instance, "updated");

        Assert.Equal("updated", getter(instance));
        Assert.Equal("updated", instance.Value);
    }

    [Fact]
    public void FieldAccessor_AccessesPrivateFieldOnValueType()
    {
        var instance = new ValueHolder(17);
        var getter = (ValueTypeGetter<ValueHolder, int>)FieldAccessor.GetValueGetter(typeof(ValueHolder), "_value");
        var setter = (ValueTypeSetter<ValueHolder, int>)FieldAccessor.GetValueSetter(typeof(ValueHolder), "_value");

        Assert.Equal(17, getter(ref instance));

        setter(ref instance, 29);

        Assert.Equal(29, getter(ref instance));
        Assert.Equal(29, instance.Value);
    }

    [Fact]
    public void GeneratedCodeHelper_FindsInheritedGenericInterfaceMethod()
    {
        var method = OrleansGeneratedCodeHelper.GetMethodInfoOrDefault(
            typeof(IDerivedContract),
            nameof(IBaseContract.Convert),
            [typeof(int)],
            [typeof(string)]);

        Assert.NotNull(method);
        Assert.Equal(typeof(IBaseContract), method.DeclaringType);
        Assert.Equal(typeof(int), method.ReturnType);
        Assert.False(method.ContainsGenericParameters);
    }

    [Fact]
    public void GeneratedCodeHelper_CreatesServiceUsingPublicConstructor()
    {
        using var serviceProvider = new ServiceCollection()
            .AddSerializer()
            .BuildServiceProvider();
        var codecProvider = serviceProvider.GetRequiredService<ICodecProvider>();

        var service = OrleansGeneratedCodeHelper.GetService<PublicConstructorService>(this, codecProvider);

        Assert.Equal(42, service.Value);
    }

    private sealed class GenericReferenceHolder<T>(T value)
    {
        private T _value = value;

        public T Value => _value;
    }

    private struct ValueHolder(int value)
    {
        private int _value = value;

        public readonly int Value => _value;
    }

    private readonly struct StructWithPrivateReference(object value)
    {
        private readonly object _value = value;

        public object Value => _value;
    }

    private interface IBaseContract
    {
        TResult Convert<TResult>(string value);
    }

    private interface IDerivedContract : IBaseContract;

    private sealed class PublicConstructorService
    {
        public PublicConstructorService()
        {
        }

        public int Value => 42;
    }
}
