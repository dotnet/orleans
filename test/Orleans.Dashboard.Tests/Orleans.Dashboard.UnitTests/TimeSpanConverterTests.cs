using System;
using System.Text.Json;
using Orleans.Dashboard;
using Xunit;

namespace UnitTests;

[TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Dashboard")]
public class TimeSpanConverterTests
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Converters = { new TimeSpanConverter() },
    };

    [Theory]
    [InlineData(0L, "\"00:00:00\"")]
    [InlineData(1_838_450_000_000L, "\"2.03:04:05\"")]
    [InlineData(1_234_567L, "\"00:00:00.1234567\"")]
    [InlineData(-110_450_000_000L, "\"-03:04:05\"")]
    public void Write_WholeAndFractionalValues_UsesConstantTimeSpanFormat(long ticks, string expectedJson)
    {
        var value = TimeSpan.FromTicks(ticks);

        var json = JsonSerializer.Serialize(value, SerializerOptions);

        Assert.Equal(expectedJson, json);
    }

    [Fact]
    public void Write_ValueThenRead_PreservesExactJsonAndTicks()
    {
        var expected = new TimeSpan(1, 2, 3, 4, 567).Add(TimeSpan.FromTicks(8_901));

        var json = JsonSerializer.Serialize(expected, SerializerOptions);
        var actual = JsonSerializer.Deserialize<TimeSpan>(json, SerializerOptions);

        Assert.Equal("\"1.02:03:04.5678901\"", json);
        Assert.Equal(expected, actual);
        Assert.Equal(937_845_678_901L, actual.Ticks);
    }

    [Theory]
    [InlineData("\"3.04:05:06.7000000\"", 2_739_067_000_000L)]
    [InlineData("\"-1.02:03:04.5000000\"", -937_845_000_000L)]
    public void Read_ValidConstantFormatString_ReturnsExpectedTimeSpan(string json, long expectedTicks)
    {
        var actual = JsonSerializer.Deserialize<TimeSpan>(json, SerializerOptions);

        Assert.Equal(TimeSpan.FromTicks(expectedTicks), actual);
        Assert.Equal(expectedTicks, actual.Ticks);
    }

    [Fact]
    public void Read_MalformedString_ThrowsFormatException()
    {
        var exception = Assert.Throws<FormatException>(
            () => Read("\"not-a-timespan\""));

        Assert.NotEmpty(exception.Message);
    }

    [Theory]
    [InlineData("42")]
    [InlineData("true")]
    [InlineData("{}")]
    [InlineData("[]")]
    public void Read_NonStringToken_ThrowsJsonException(string json)
    {
        var exception = Assert.Throws<JsonException>(
            () => JsonSerializer.Deserialize<TimeSpan>(json, SerializerOptions));

        Assert.Equal("$", exception.Path);
        Assert.Contains(
            "The JSON value could not be converted to System.TimeSpan.",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Read_NullToken_ThrowsArgumentNullException()
    {
        var exception = Assert.Throws<ArgumentNullException>(
            () => Read("null"));

        Assert.Equal("input", exception.ParamName);
    }

    private static TimeSpan Read(string json)
    {
        var reader = new Utf8JsonReader(System.Text.Encoding.UTF8.GetBytes(json));
        Assert.True(reader.Read());
        return new TimeSpanConverter().Read(ref reader, typeof(TimeSpan), SerializerOptions);
    }
}
