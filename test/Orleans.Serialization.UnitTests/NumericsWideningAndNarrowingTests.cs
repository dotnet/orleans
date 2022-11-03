using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Orleans.Serialization.UnitTests;

/// <summary>
/// Ensures that numeric fields (integers, floats, etc) can be widened and narrowed in a version tolerant manner.
/// </summary>
public class NumericsWideningAndNarrowingTests
{
    private readonly IServiceProvider _serviceProvider;
    private readonly Serializer _serializer;

    public NumericsWideningAndNarrowingTests()
    {
        var services = new ServiceCollection();
        _ = services.AddSerializer();
        _serviceProvider = services.BuildServiceProvider();
        _serializer = _serviceProvider.GetRequiredService<Serializer>();
    }

    /// <summary>
    /// Tests that unsigned integers can be widened and narrowed and maintain compatibility.
    /// </summary>
    [Fact]
    public void UnsignedIntegerWideningAndNarrowingVersionToleranceTests()
    {
        ConversionRoundTrip<byte, ushort>();
        ConversionRoundTrip<byte, uint>();
        ConversionRoundTrip<byte, ulong>();
        ConversionRoundTrip<byte, UInt128>();

        ConversionRoundTrip<ushort, uint>();
        ConversionRoundTrip<ushort, ulong>();
        ConversionRoundTrip<ushort, UInt128>();

        ConversionRoundTrip<uint, ulong>();
        ConversionRoundTrip<uint, UInt128>();

        ConversionRoundTrip<ulong, UInt128>();
    }

    /// <summary>
    /// Tests that signed integers can be widened and narrowed and maintain compatibility.
    /// </summary>
    [Fact]
    public void SignedIntegerWideningAndNarrowingVersionToleranceTests()
    {
        ConversionRoundTrip<sbyte, short>();
        ConversionRoundTrip<sbyte, int>();
        ConversionRoundTrip<sbyte, long>();
        ConversionRoundTrip<sbyte, Int128>();

        ConversionRoundTrip<short, int>();
        ConversionRoundTrip<short, long>();
        ConversionRoundTrip<short, Int128>();

        ConversionRoundTrip<int, long>();
        ConversionRoundTrip<int, Int128>();

        ConversionRoundTrip<long, Int128>();
    }

    /// <summary>
    /// Tests that floating point numbers can be widened and narrowed and maintain compatibility.
    /// </summary>
    [Fact]
    public void FloatWideningAndNarrowingVersionToleranceTests()
    {
        FloatConversionRoundTrip<Half, decimal>();
        FloatConversionRoundTrip<Half, float>();
        FloatConversionRoundTrip<Half, double>();

        FloatConversionRoundTrip<decimal, float>();
        FloatConversionRoundTrip<decimal, double>();

        FloatConversionRoundTrip<float, double>();
    }

    private void ConversionRoundTrip<N, W>()
        where N : INumber<N>, IMinMaxValue<N>
        where W : INumber<W>, IMinMaxValue<W>
    {
        WideningRoundTrip<N, W>();
        NarrowingRoundTrip<N, W>();
        NarrowingOverflow<N, W>();
    }

    private void FloatConversionRoundTrip<N, W>()
        where N : INumber<N>, IMinMaxValue<N>
        where W : INumber<W>, IMinMaxValue<W>
    {
        FloatWideningRoundTrip<N, W>();
        FloatNarrowingRoundTrip<N, W>();
        NarrowingOverflow<N, W>();
    }

    private void WideningRoundTrip<N, W>()
        where N : INumber<N>, IMinMaxValue<N>
        where W : INumber<W>, IMinMaxValue<W>
    {
        var two = N.MultiplicativeIdentity + N.MultiplicativeIdentity;
        var values = new List<N>
        {
            N.MinValue,
            default(N),
            N.MaxValue / two,
            N.MaxValue,
        };

        foreach (var value in values)
        {
            RoundTripValue<N, W>(value);
        }
    }

    private void NarrowingRoundTrip<N, W>()
        where N : INumber<N>, IMinMaxValue<N>
        where W : INumber<W>, IMinMaxValue<W>
    {
        var two = N.MultiplicativeIdentity + N.MultiplicativeIdentity;
        var values = (new N[]
        {
            N.MinValue,
            default(N),
            N.MaxValue / two,
            N.MaxValue,
        }).Select(W.CreateTruncating).ToList();

        foreach (var value in values)
        {
            RoundTripValue<W, N>(value);
        }
    }

    private void NarrowingOverflow<N, W>()
        where N : INumber<N>, IMinMaxValue<N>
        where W : INumber<W>, IMinMaxValue<W>
    {
        var values = W.Sign(W.MinValue) switch
        {
            -1 => new[] { W.MinValue, W.MaxValue },
            _ => new[] { W.MaxValue }
        };

        foreach (var value in values)
        {
            Assert.Throws<OverflowException>(() => RoundTripValueDirectly<W, N>(value));
            Assert.Throws<OverflowException>(() => RoundTripValueIndirectly<W, N>(value));
        }
    }

    private void FloatWideningRoundTrip<N, W>()
        where N : INumber<N>, IMinMaxValue<N>
        where W : INumber<W>, IMinMaxValue<W>
    {
        var two = N.MultiplicativeIdentity + N.MultiplicativeIdentity;
        var buffer = N.MaxValue / (two * two * two);
        var values = new List<N>
        {
            N.MinValue + buffer,
            N.MinValue / two,
            default(N),
            N.MaxValue / two,
            N.MaxValue - buffer,
        };

        foreach (var value in values)
        {
            RoundTripValue<N, W>(value);
        }
    }

    private void FloatNarrowingRoundTrip<N, W>()
        where N : INumber<N>, IMinMaxValue<N>
        where W : INumber<W>, IMinMaxValue<W>
    {
        var two = N.MultiplicativeIdentity + N.MultiplicativeIdentity;
        var buffer = N.MaxValue / (two * two * two);
        var values = new List<N>
        {
            N.MinValue + buffer,
            N.MinValue / two,
            default(N),
            N.MaxValue / two,
            N.MaxValue - buffer,
        }.Select(W.CreateTruncating).ToList();

        foreach (var value in values)
        {
            RoundTripValue<W, N>(value);
        }
    }

    private void RoundTripValue<TLeft, TRight>(TLeft leftValue)
        where TLeft : INumber<TLeft>, IMinMaxValue<TLeft>
        where TRight : INumber<TRight>, IMinMaxValue<TRight>
    {
        RoundTripValueDirectly<TLeft, TRight>(leftValue);
        RoundTripValueIndirectly<TLeft, TRight>(leftValue);
    }

    private void RoundTripValueDirectly<TLeft, TRight>(TLeft leftValue)
        where TLeft : INumber<TLeft>, IMinMaxValue<TLeft>
        where TRight : INumber<TRight>, IMinMaxValue<TRight>
    {
        // Round-trip the value, converting it along the way.
        var payload = _serializer.SerializeToArray(leftValue);
        var result = _serializer.Deserialize<TRight>(payload);

        var asRight = TRight.CreateTruncating(leftValue);
        Assert.Equal(asRight, result);

        var asLeft = TLeft.CreateTruncating(result);
        var expected = TLeft.CreateTruncating(TRight.CreateTruncating(leftValue));
        Assert.Equal(expected, asLeft);
    }

    private void RoundTripValueIndirectly<TLeft, TRight>(TLeft leftValue)
        where TLeft : INumber<TLeft>, IMinMaxValue<TLeft>
        where TRight : INumber<TRight>, IMinMaxValue<TRight>
    {
        // Wrap the value and round-trip the wrapped value, converting it along the way.
        var payload = _serializer.SerializeToArray(new ValueHolder<TLeft> { Value = leftValue });
        var result = _serializer.Deserialize<ValueHolder<TRight>>(payload).Value;

        var asRight = TRight.CreateTruncating(leftValue);
        Assert.Equal(asRight, result);

        var asLeft = TLeft.CreateTruncating(result);
        var expected = TLeft.CreateTruncating(TRight.CreateTruncating(leftValue));
        Assert.Equal(expected, asLeft);
    }

    [GenerateSerializer]
    public sealed class ValueHolder<T>
    {
        [Id(0)]
        public T Value;
    }
}

