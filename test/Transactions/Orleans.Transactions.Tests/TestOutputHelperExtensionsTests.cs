using System;
using System.Reflection;
using Orleans.Transactions.TestKit.xUnit;
using Xunit;

namespace UnitTests.Transactions;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Transactions")]
public sealed class TestOutputHelperExtensionsTests
{
    [Fact]
    public void GetWriteLine_NullOutput_ThrowsWithRequestedParameterName()
    {
        var helperType = typeof(ConsistencyTransactionTestRunnerxUnit).Assembly.GetType(
            "Orleans.Transactions.TestKit.xUnit.TestOutputHelperExtensions",
            throwOnError: true)!;
        var method = helperType.GetMethod("GetWriteLine", BindingFlags.Public | BindingFlags.Static)!;

        var exception = Assert.Throws<TargetInvocationException>(
            () => method.Invoke(null, [null, "testOutput"]));

        var argumentException = Assert.IsType<ArgumentNullException>(exception.InnerException);
        Assert.Equal("testOutput", argumentException.ParamName);
    }
}
