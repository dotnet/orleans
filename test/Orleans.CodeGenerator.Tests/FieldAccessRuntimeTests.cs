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
        Assert.Equal(typeof(Action<ConstrainedFields<string>, string>), codecType.GetField("setField_0", flags)!.FieldType);
        Assert.Null(codecType.GetMethod("setField_0", flags));
#else
        Assert.Null(codecType.GetField("setField_0", flags));
        var method = codecType.GetMethod("setField_0", flags)!;
        Assert.Equal(typeof(string).MakeByRefType(), method.ReturnType);
        var accessor = method.GetCustomAttribute<UnsafeAccessorAttribute>()!;
        Assert.Equal(UnsafeAccessorKind.Field, accessor.Kind);
        Assert.Equal("_value", accessor.Name);
#endif
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
