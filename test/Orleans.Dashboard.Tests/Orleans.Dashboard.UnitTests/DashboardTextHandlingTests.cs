using System;
using System.Globalization;
using Orleans.Dashboard.Core;
using Orleans.Dashboard.Implementation.Helpers;
using Orleans.Dashboard.Metrics.History;
using Orleans.Dashboard.Model;
using TestGrains;
using Xunit;

namespace UnitTests;

[TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Dashboard")]
public class DashboardTextHandlingTests
{
    [Fact]
    public void ToPeriodString_UsesInvariantCalendar()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            var period = new DateTime(2024, 1, 2, 3, 4, 5);

            var result = period.ToPeriodString();

            Assert.Equal("2024-01-02T03:04:05", result);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Theory]
    [InlineData(typeof(TestStateGrain), "-42", "")]
    [InlineData(typeof(TestStateCompoundKeyGrain), "-42,tenant", "tenant")]
    public void GetGrainId_ParsesIntegerKeysUsingInvariantCulture(Type implementationType, string id, string expectedKeyExtension)
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
            culture.NumberFormat.NegativeSign = "~";
            CultureInfo.CurrentCulture = culture;

            var (grainId, keyExtension) = GrainStateHelper.GetGrainId(id, implementationType);

            Assert.Equal(-42L, grainId);
            Assert.Equal(expectedKeyExtension, keyExtension);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public void GrainTraceEqualityComparer_UsesCaseInsensitiveOrdinalHashCodes()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            var upper = new GrainTraceEntry
            {
                Grain = "GRAIN-I",
                Method = "METHOD-I",
                SiloAddress = "SILO-I",
            };
            var lower = new GrainTraceEntry
            {
                Grain = "grain-i",
                Method = "method-i",
                SiloAddress = "silo-i",
            };

            AssertComparerContract(GrainTraceEqualityComparer.ByGrainAndMethod, upper, lower);
            AssertComparerContract(GrainTraceEqualityComparer.ByGrainAndMethodAndSilo, upper, lower);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    private static void AssertComparerContract(
        GrainTraceEqualityComparer comparer,
        GrainTraceEntry first,
        GrainTraceEntry second)
    {
        Assert.True(comparer.Equals(first, second));
        Assert.Equal(comparer.GetHashCode(first), comparer.GetHashCode(second));
    }
}
