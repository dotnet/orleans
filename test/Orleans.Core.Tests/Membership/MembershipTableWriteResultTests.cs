using System.Reflection;
using System.Runtime.CompilerServices;
using Orleans;
using Orleans.Concurrency;
using TestExtensions;
using Xunit;

namespace NonSilo.Tests.Membership;

[TestCategory("BVT"), TestCategory("Membership")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Runtime")]
public class MembershipTableWriteResultTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Result_ValidStates_PreserveOutcomeAndReceipt(bool succeeded, bool includeReceipt)
    {
        var receipt = includeReceipt
            ? new MembershipTableWriteReceipt(new TableVersion(73, "table/commit-73"), "row/commit-29")
            : null;

        var result = new MembershipTableWriteResult(succeeded, receipt);

        Assert.Equal(succeeded, result.Succeeded);
        if (includeReceipt)
        {
            Assert.Same(receipt, result.Receipt);
        }
        else
        {
            Assert.Null(result.Receipt);
        }
    }

    [Fact]
    public void Result_FailureWithReceipt_Throws()
    {
        var receipt = new MembershipTableWriteReceipt(new TableVersion(73, "table/commit-73"), "row/commit-29");

        var exception = Assert.Throws<ArgumentException>(() => new MembershipTableWriteResult(false, receipt));

        Assert.Equal("receipt", exception.ParamName);
    }

    [Fact]
    public void Result_Default_IsFailureWithoutReceipt()
    {
        var result = default(MembershipTableWriteResult);

        Assert.False(result.Succeeded);
        Assert.Null(result.Receipt);
    }

    [Theory]
    [InlineData(true, "version")]
    [InlineData(false, "rowETag")]
    public void Receipt_NullArgument_Throws(bool nullVersion, string expectedParameterName)
    {
        var version = nullVersion ? null : new TableVersion(73, "table/commit-73");
        var rowETag = nullVersion ? "row/commit-29" : null;

        var exception = Assert.Throws<ArgumentNullException>(() => new MembershipTableWriteReceipt(version!, rowETag!));

        Assert.Equal(expectedParameterName, exception.ParamName);
    }

    [Theory]
    [InlineData(37, "table-etag/0042", "row-etag/0091")]
    [InlineData(0, "", "row/nonempty")]
    [InlineData(71, "table/nonempty", "")]
    [InlineData(105, " \t", "\r\n ")]
    [InlineData(int.MaxValue, "W/\"table:\u8868/\u03B1==\"", "\"row:\u884C/\u03B2+=\"")]
    public void Receipt_PreservesOpaqueTags(int versionNumber, string tableETag, string rowETag)
    {
        var version = new TableVersion(versionNumber, tableETag);

        var receipt = new MembershipTableWriteReceipt(version, rowETag);

        Assert.Same(version, receipt.Version);
        Assert.Equal(versionNumber, receipt.Version.Version);
        Assert.Equal(tableETag, receipt.Version.VersionEtag);
        Assert.Equal(rowETag, receipt.RowETag);
    }

    [Fact]
    public void ContractTypes_HaveImmutableSerializedShape()
    {
        var resultType = typeof(MembershipTableWriteResult);
        var receiptType = typeof(MembershipTableWriteReceipt);

        Assert.True(resultType.IsValueType);
        Assert.NotNull(resultType.GetCustomAttribute<IsReadOnlyAttribute>());
        Assert.True(receiptType.IsClass);
        Assert.True(receiptType.IsSealed);
        foreach (var contractType in new[] { resultType, receiptType })
        {
            Assert.True(contractType.IsPublic);
            Assert.NotNull(contractType.GetCustomAttribute<ImmutableAttribute>());
            Assert.NotNull(contractType.GetCustomAttribute<GenerateSerializerAttribute>());
        }

        foreach (var (declaringType, name, propertyType, id) in new[]
        {
            (resultType, nameof(MembershipTableWriteResult.Succeeded), typeof(bool), 0U),
            (resultType, nameof(MembershipTableWriteResult.Receipt), receiptType, 1U),
            (receiptType, nameof(MembershipTableWriteReceipt.Version), typeof(TableVersion), 0U),
            (receiptType, nameof(MembershipTableWriteReceipt.RowETag), typeof(string), 1U),
        })
        {
            var property = declaringType.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            Assert.NotNull(property);
            Assert.Equal(propertyType, property.PropertyType);
            Assert.NotNull(property.GetGetMethod(nonPublic: false));
            Assert.Null(property.GetSetMethod(nonPublic: true));
            Assert.Equal(id, Assert.Single(property.GetCustomAttributes<IdAttribute>()).Id);
        }

        var resultConstructor = Assert.Single(resultType.GetConstructors());
        Assert.Collection(resultConstructor.GetParameters(),
            parameter =>
            {
                Assert.Equal("succeeded", parameter.Name);
                Assert.Equal(typeof(bool), parameter.ParameterType);
                Assert.False(parameter.IsOptional);
                Assert.False(parameter.HasDefaultValue);
            },
            parameter =>
            {
                Assert.Equal("receipt", parameter.Name);
                Assert.Equal(receiptType, parameter.ParameterType);
                Assert.True(parameter.IsOptional);
                Assert.True(parameter.HasDefaultValue);
                Assert.Null(parameter.DefaultValue);
            });

        var receiptConstructor = Assert.Single(receiptType.GetConstructors());
        Assert.Collection(receiptConstructor.GetParameters(),
            parameter =>
            {
                Assert.Equal("version", parameter.Name);
                Assert.Equal(typeof(TableVersion), parameter.ParameterType);
                Assert.False(parameter.IsOptional);
                Assert.False(parameter.HasDefaultValue);
            },
            parameter =>
            {
                Assert.Equal("rowETag", parameter.Name);
                Assert.Equal(typeof(string), parameter.ParameterType);
                Assert.False(parameter.IsOptional);
                Assert.False(parameter.HasDefaultValue);
            });
    }
}
