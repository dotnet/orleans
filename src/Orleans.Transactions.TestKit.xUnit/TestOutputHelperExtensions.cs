using System;
using Xunit;

namespace Orleans.Transactions.TestKit.xUnit;

internal static class TestOutputHelperExtensions
{
    public static Action<string> GetWriteLine(ITestOutputHelper output, string paramName)
    {
        ArgumentNullException.ThrowIfNull(output, paramName);
        return output.WriteLine;
    }
}
