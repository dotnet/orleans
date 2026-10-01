using System.Reflection;
using System.Runtime.CompilerServices;
using Orleans.Serialization.NativeAotFieldAccessSmoke;
using Xunit;

namespace Orleans.CodeGenerator.Tests;

public class FieldAccessRuntimeTests
{
    [Fact]
    public void GeneratedGenericAccessorShapeMatchesTargetFramework()
    {
        var codecType = typeof(OrleansCodeGen.Orleans.Serialization.NativeAotFieldAccessSmoke.Codec_ConstrainedFields<string>);
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
#if NET8_0
        Assert.Equal(typeof(Func<ConstrainedFields<string>, string>), codecType.GetField("getField_0", flags)!.FieldType);
        Assert.Equal(typeof(Action<ConstrainedFields<string>, string>), codecType.GetField("setField_0", flags)!.FieldType);
        Assert.Null(codecType.GetMethod("setField_0", flags));
        Assert.Null(codecType.GetMethod("accessField_0", flags));
#else
        Assert.Null(codecType.GetField("setField_0", flags));
        Assert.Null(codecType.GetMethod("getField_0", flags));
        Assert.Null(codecType.GetMethod("setField_0", flags));
        var method = codecType.GetMethod("accessField_0", flags)!;
        Assert.Equal(typeof(string).MakeByRefType(), method.ReturnType);
        var accessor = method.GetCustomAttribute<UnsafeAccessorAttribute>()!;
        Assert.Equal(UnsafeAccessorKind.Field, accessor.Kind);
        Assert.Equal("_value", accessor.Name);
#endif
    }

    [Fact]
    public void GeneratedPrivateFieldAccessorsAreDeclaredOncePerField()
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        foreach (var type in new[]
        {
            typeof(OrleansCodeGen.Orleans.Serialization.NativeAotFieldAccessSmoke.Codec_PrivateFields),
            typeof(OrleansCodeGen.Orleans.Serialization.NativeAotFieldAccessSmoke.Copier_PrivateFields),
        })
        {
            var accessors = type.GetMethods(flags)
                .Select(method => method.GetCustomAttribute<UnsafeAccessorAttribute>())
                .Where(attribute => attribute is not null)
                .ToList();
            Assert.Equal(6, accessors.Count);
            Assert.Equal(6, accessors.Select(attribute => attribute!.Name).Distinct(StringComparer.Ordinal).Count());
            Assert.All(accessors, attribute => Assert.Equal(UnsafeAccessorKind.Field, attribute!.Kind));
        }
    }

    [Fact]
    public void PrivateAndBackingFieldsRoundTripAndCopy() => FieldAccessChecks.PrivateAndBackingFieldsRoundTripAndCopy();

    [Fact]
    public void ConstrainedGenericFieldsRoundTripAndCopy() => FieldAccessChecks.ConstrainedGenericFieldsRoundTripAndCopy();

    [Fact]
    public void GenericStructFieldsRoundTripAndCopy() => FieldAccessChecks.GenericStructFieldsRoundTripAndCopy();

    [Fact]
    public void NestedGenericFieldsRoundTripAndCopy() => FieldAccessChecks.NestedGenericFieldsRoundTripAndCopy();
}
