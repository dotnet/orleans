using Orleans.Connections.Transport.Security;
using Xunit;

namespace Orleans.Connections.Security.Tests;

public class TlsOptionsTests
{
    [Fact]
    public void HandshakeTimeout_InfiniteTimeSpan_RoundTripsAndCreatesUntimedTokenSource()
    {
        var options = new TlsOptions
        {
            HandshakeTimeout = Timeout.InfiniteTimeSpan
        };
        var copiedOptions = new TlsOptions
        {
            HandshakeTimeout = options.HandshakeTimeout
        };

        Assert.Equal(Timeout.InfiniteTimeSpan, options.HandshakeTimeout);
        Assert.Equal(Timeout.InfiniteTimeSpan, copiedOptions.HandshakeTimeout);
        using var cancellationTokenSource = options.CreateHandshakeCancellationTokenSource();
        Assert.False(cancellationTokenSource.IsCancellationRequested);
    }

    [Fact]
    public void HandshakeTimeout_MaximumSupportedFiniteValue_CreatesCancelableTokenSource()
    {
        var maximum = TimeSpan.FromMilliseconds(uint.MaxValue - 1);
        var options = new TlsOptions
        {
            HandshakeTimeout = maximum
        };

        Assert.Equal(maximum, options.HandshakeTimeout);
        using var cancellationTokenSource = options.CreateHandshakeCancellationTokenSource();
        Assert.True(cancellationTokenSource.Token.CanBeCanceled);
    }

    [Fact]
    public void HandshakeTimeout_FirstUnsupportedFiniteValue_ThrowsArgumentOutOfRangeException()
    {
        var options = new TlsOptions();
        var rejectedValue = TimeSpan.FromMilliseconds(uint.MaxValue);

        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => options.HandshakeTimeout = rejectedValue);

        Assert.Equal("value", exception.ParamName);
        Assert.Contains("must be positive and no greater than", exception.Message);
        Assert.Equal(TimeSpan.FromSeconds(10), options.HandshakeTimeout);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void HandshakeTimeout_NonPositiveFiniteValue_ThrowsArgumentOutOfRangeException(int ticks)
    {
        var options = new TlsOptions();

        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => options.HandshakeTimeout = TimeSpan.FromTicks(ticks));

        Assert.Equal("value", exception.ParamName);
        Assert.Contains("HandshakeTimeout must be positive", exception.Message);
        Assert.Equal(TimeSpan.FromSeconds(10), options.HandshakeTimeout);
    }
}
