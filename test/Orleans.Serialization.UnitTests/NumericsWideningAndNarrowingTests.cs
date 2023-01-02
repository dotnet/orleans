#if NET7_0_OR_GREATER
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
#else
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Orleans.Serialization.UnitTests;

/// <summary>
/// Ensures that numeric fields (integers, floats, etc) can be widened and narrowed in a version tolerant manner.
/// </summary>
public class NumericsWideningAndNarrowingTests
{
    private static readonly ByteNumber ByteType = new();
    private static readonly ShortNumber ShortType = new();
    private static readonly IntNumber IntType = new();
    private static readonly LongNumber LongType = new();
    private static readonly SByteNumber SByteType = new();
    private static readonly UShortNumber UShortType = new();
    private static readonly UIntNumber UIntType = new();
    private static readonly ULongNumber ULongType = new();
    private static readonly FloatNumber FloatType = new();
    private static readonly DoubleNumber DoubleType = new();
    private static readonly DecimalNumber DecimalType = new();
#if NET5_0_OR_GREATER
    private static readonly HalfNumber HalfType = new();
#endif

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
        ConversionRoundTrip(ByteType, UShortType);
        ConversionRoundTrip(ByteType, UIntType);
        ConversionRoundTrip(ByteType, ULongType);

        ConversionRoundTrip(UShortType, UIntType);
        ConversionRoundTrip(UShortType, ULongType);

        ConversionRoundTrip(UIntType, ULongType);
    }

    /// <summary>
    /// Tests that signed integers can be widened and narrowed and maintain compatibility.
    /// </summary>
    [Fact]
    public void SignedIntegerWideningAndNarrowingVersionToleranceTests()
    {
        ConversionRoundTrip(SByteType, ShortType);
        ConversionRoundTrip(SByteType, IntType);
        ConversionRoundTrip(SByteType, LongType);

        ConversionRoundTrip(ShortType, IntType);
        ConversionRoundTrip(ShortType, LongType);

        ConversionRoundTrip(IntType, LongType);
    }

    /// <summary>
    /// Tests that floating point numbers can be widened and narrowed and maintain compatibility.
    /// </summary>
    [Fact]
    public void FloatWideningAndNarrowingVersionToleranceTests()
    {
#if NET5_0_OR_GREATER
        FloatConversionRoundTrip(HalfType, DecimalType);
        FloatConversionRoundTrip(HalfType, FloatType);
        FloatConversionRoundTrip(HalfType, DoubleType);
#endif

        FloatConversionRoundTrip(DecimalType, FloatType);
        FloatConversionRoundTrip(DecimalType, DoubleType);

        FloatConversionRoundTrip(FloatType, DoubleType);
    }

    private void ConversionRoundTrip<N, W>(INumber<N> n, INumber<W> w)
    {
        WideningRoundTrip(n, w);
        NarrowingRoundTrip(n, w);
        NarrowingOverflow(n, w);
    }

    private void FloatConversionRoundTrip<N, W>(INumber<N> n, INumber<W> w)
    {
        FloatWideningRoundTrip(n, w);
        FloatNarrowingRoundTrip(n, w);
        NarrowingOverflow(n, w);
    }

    private void WideningRoundTrip<N, W>(INumber<N> n, INumber<W> w)
    {
        var two = n.Add(n.MultiplicativeIdentity, n.MultiplicativeIdentity);
        var values = new List<N>
        {
            n.MinValue,
            default(N),
            n.Divide(n.MaxValue, two),
            n.MaxValue,
        };

        foreach (var value in values)
        {
            RoundTripValue(value, n, w);
        }
    }

    private void NarrowingRoundTrip<N, W>(INumber<N> n, INumber<W> w)
    {
        var two = n.Add(n.MultiplicativeIdentity, n.MultiplicativeIdentity);
        var values = (new N[]
        {
            n.MinValue,
            default(N),
            n.Divide(n.MaxValue, two),
            n.MaxValue,
        }).Select(w.CreateTruncating).ToList();

        foreach (var value in values)
        {
            RoundTripValue(value, w, n);
        }
    }

    private void NarrowingOverflow<N, W>(INumber<N> n, INumber<W> w)
    {
        var values = w.Sign(w.MinValue) switch
        {
            -1 => new[] { w.MinValue, w.MaxValue },
            _ => new[] { w.MaxValue }
        };

        foreach (var value in values)
        {
            Assert.Throws<OverflowException>(() => RoundTripValueDirectly(value, w, n));
            Assert.Throws<OverflowException>(() => RoundTripValueIndirectly(value, w, n));
        }
    }

    private void FloatWideningRoundTrip<N, W>(INumber<N> n, INumber<W> w)
    {
        var two = n.Add(n.MultiplicativeIdentity, n.MultiplicativeIdentity);
        var buffer = n.Divide(n.Divide(n.Divide(n.MaxValue, two), two), two);
        var values = new List<N>
        {
            n.Add(n.MinValue, buffer),
            n.Divide(n.MinValue, two),
            default(N),
            n.Divide(n.MaxValue, two),
            n.Subtract(n.MaxValue, buffer),
        };

        foreach (var value in values)
        {
            RoundTripValue(value, n, w);
        }
    }

    private void FloatNarrowingRoundTrip<N, W>(INumber<N> n, INumber<W> w)
    {
        var two = n.Add(n.MultiplicativeIdentity, n.MultiplicativeIdentity);
        var buffer = n.Divide(n.Divide(n.Divide(n.MaxValue, two), two), two);
        var values = new List<N>
        {
            n.Add(n.MinValue, buffer),
            n.Divide(n.MinValue, two),
            default(N),
            n.Divide(n.MaxValue, two),
            n.Subtract(n.MaxValue, buffer),
        }.Select(w.CreateTruncating).ToList();

        foreach (var value in values)
        {
            RoundTripValue(value, w, n);
        }
    }

    private void RoundTripValue<TLeft, TRight>(TLeft leftValue, INumber<TLeft> left, INumber<TRight> right)
    {
        RoundTripValueDirectly(leftValue, left, right);
        RoundTripValueIndirectly(leftValue, left, right);
    }

    private void RoundTripValueDirectly<TLeft, TRight>(TLeft leftValue, INumber<TLeft> left, INumber<TRight> right)
    {
        // Round-trip the value, converting it along the way.
        var payload = _serializer.SerializeToArray(leftValue);
        var result = _serializer.Deserialize<TRight>(payload);

        var asRight = right.CreateTruncating(leftValue);
        Assert.Equal(asRight, result);

        var asLeft = left.CreateTruncating(result);
        var expected = left.CreateTruncating(right.CreateTruncating(leftValue));
        Assert.Equal(expected, asLeft);
    }

    private void RoundTripValueIndirectly<TLeft, TRight>(TLeft leftValue, INumber<TLeft> left, INumber<TRight> right)
    {
        // Wrap the value and round-trip the wrapped value, converting it along the way.
        var payload = _serializer.SerializeToArray(new ValueHolder<TLeft> { Value = leftValue });
        var result = _serializer.Deserialize<ValueHolder<TRight>>(payload).Value;

        var asRight = right.CreateTruncating(leftValue);
        Assert.Equal(asRight, result);

        var asLeft = left.CreateTruncating(result);
        var expected = left.CreateTruncating(right.CreateTruncating(leftValue));
        Assert.Equal(expected, asLeft);
    }

    [GenerateSerializer]
    public sealed class ValueHolder<T>
    {
        [Id(0)]
        public T Value;
    }

    public interface INumber<T>
    {
        public T MinValue { get; }
        public T MaxValue { get; }
        public T MultiplicativeIdentity { get; }
        public int Sign(T value);
        public T Add(T x, T y);
        public T Subtract(T x, T y);
        public T Divide(T x, T y);
        public T CreateTruncating<TFrom>(TFrom from);
    }

    public sealed class ByteNumber : INumber<byte>
    {
        public byte MinValue => byte.MinValue;
        public byte MaxValue => byte.MaxValue;
        public byte MultiplicativeIdentity => 1;
        public int Sign(byte value) => Math.Sign(value);

        public byte Add(byte x, byte y) => (byte)(x + y);

        public byte Subtract(byte x, byte y) => (byte)(x - y);

        public byte Divide(byte x, byte y) => (byte)(x / y);

        public byte CreateTruncating<TFrom>(TFrom from) => (byte)Convert.ChangeType(from, typeof(byte));
    }

    public sealed class UShortNumber : INumber<ushort>
    {
        public ushort MinValue => ushort.MinValue;
        public ushort MaxValue => ushort.MaxValue;
        public ushort MultiplicativeIdentity => 1;
        public int Sign(ushort value) => 1;

        public ushort Add(ushort x, ushort y) => (ushort)(x + y);

        public ushort Subtract(ushort x, ushort y) => (ushort)(x - y);

        public ushort Divide(ushort x, ushort y) => (ushort)(x / y);

        public ushort CreateTruncating<TFrom>(TFrom from) => (ushort)Convert.ChangeType(from, typeof(ushort));
    }

    public sealed class UIntNumber : INumber<uint>
    {
        public uint MinValue => uint.MinValue;
        public uint MaxValue => uint.MaxValue;
        public uint MultiplicativeIdentity => 1;
        public int Sign(uint value) => 1;

        public uint Add(uint x, uint y) => x + y;

        public uint Subtract(uint x, uint y) => x - y;

        public uint Divide(uint x, uint y) => x / y;

        public uint CreateTruncating<TFrom>(TFrom from) => (uint)Convert.ChangeType(from, typeof(uint));
    }

    public sealed class ULongNumber : INumber<ulong>
    {
        public ulong MinValue => ulong.MinValue;
        public ulong MaxValue => ulong.MaxValue;
        public ulong MultiplicativeIdentity => 1;
        public int Sign(ulong value) => 1;

        public ulong Add(ulong x, ulong y) => x + y;

        public ulong Subtract(ulong x, ulong y) => x - y;

        public ulong Divide(ulong x, ulong y) => x / y;

        public ulong CreateTruncating<TFrom>(TFrom from) => (ulong)Convert.ChangeType(from, typeof(ulong));
    }

    public sealed class SByteNumber : INumber<sbyte>
    {
        public sbyte MinValue => sbyte.MinValue;
        public sbyte MaxValue => sbyte.MaxValue;
        public sbyte MultiplicativeIdentity => 1;
        public int Sign(sbyte value) => Math.Sign(value);

        public sbyte Add(sbyte x, sbyte y) => (sbyte)(x + y);

        public sbyte Subtract(sbyte x, sbyte y) => (sbyte)(x - y);

        public sbyte Divide(sbyte x, sbyte y) => (sbyte)(x / y);

        public sbyte CreateTruncating<TFrom>(TFrom from) => (sbyte)Convert.ChangeType(from, typeof(sbyte));
    }

    public sealed class ShortNumber : INumber<short>
    {
        public short MinValue => short.MinValue;
        public short MaxValue => short.MaxValue;
        public short MultiplicativeIdentity => 1;
        public int Sign(short value) => Math.Sign(value);

        public short Add(short x, short y) => (short)(x + y);

        public short Subtract(short x, short y) => (short)(x - y);

        public short Divide(short x, short y) => (short)(x / y);

        public short CreateTruncating<TFrom>(TFrom from) => (short)Convert.ChangeType(from, typeof(short));
    }

    public sealed class IntNumber : INumber<int>
    {
        public int MinValue => int.MinValue;
        public int MaxValue => int.MaxValue;
        public int MultiplicativeIdentity => 1;
        public int Sign(int value) => Math.Sign(value);

        public int Add(int x, int y) => x + y;

        public int Subtract(int x, int y) => x - y;

        public int Divide(int x, int y) => x / y;

        public int CreateTruncating<TFrom>(TFrom from) => (int)Convert.ChangeType(from, typeof(int));
    }

    public sealed class LongNumber : INumber<long>
    {
        public long MinValue => long.MinValue;
        public long MaxValue => long.MaxValue;
        public long MultiplicativeIdentity => 1;
        public int Sign(long value) => Math.Sign(value);

        public long Add(long x, long y) => x + y;

        public long Subtract(long x, long y) => x - y;

        public long Divide(long x, long y) => x / y;

        public long CreateTruncating<TFrom>(TFrom from) => (long)Convert.ChangeType(from, typeof(long));
    }

    public sealed class FloatNumber : INumber<float>
    {
        public float MinValue => float.MinValue;
        public float MaxValue => float.MaxValue;
        public float MultiplicativeIdentity => 1;
        public int Sign(float value) => Math.Sign(value);

        public float Add(float x, float y) => x + y;

        public float Subtract(float x, float y) => x - y;

        public float Divide(float x, float y) => x / y;

        public float CreateTruncating<TFrom>(TFrom from) => (float)Convert.ChangeType(from, typeof(float));
    }

    public sealed class DoubleNumber : INumber<double>
    {
        public double MinValue => double.MinValue;
        public double MaxValue => double.MaxValue;
        public double MultiplicativeIdentity => 1;
        public int Sign(double value) => Math.Sign(value);

        public double Add(double x, double y) => x + y;

        public double Subtract(double x, double y) => x - y;

        public double Divide(double x, double y) => x / y;

        public double CreateTruncating<TFrom>(TFrom from) => (double)Convert.ChangeType(from, typeof(double));
    }

    public sealed class DecimalNumber : INumber<decimal>
    {
        public decimal MinValue => decimal.MinValue;
        public decimal MaxValue => decimal.MaxValue;
        public decimal MultiplicativeIdentity => 1;
        public int Sign(decimal value) => Math.Sign(value);

        public decimal Add(decimal x, decimal y) => x + y;

        public decimal Subtract(decimal x, decimal y) => x - y;

        public decimal Divide(decimal x, decimal y) => x / y;

        public decimal CreateTruncating<TFrom>(TFrom from) => (decimal)Convert.ChangeType(from, typeof(decimal));
    }

#if NET5_0_OR_GREATER
    public sealed class HalfNumber : INumber<Half>
    {
        public Half MinValue => Half.MinValue;
        public Half MaxValue => Half.MaxValue;
        public Half MultiplicativeIdentity => 1;
        public int Sign(Half value) => Math.Sign(value);

        public Half Add(Half x, Half y) => x + y;

        public Half Subtract(Half x, Half y) => x - y;

        public Half Divide(Half x, Half y) => x / y;

        public Half CreateTruncating<TFrom>(TFrom from) => (Half)Convert.ChangeType(from, typeof(Half));
    }
#endif
}

#endif