using CsCheck;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.TestKit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.FSharp.Core;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Net;
using Xunit;
using Microsoft.FSharp.Collections;
using Xunit.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.Serialization.Configuration;
using System.Reflection;
using System.CodeDom.Compiler;
using System.Collections;
using Orleans.Serialization.Invocation;
using System.Globalization;

namespace Orleans.Serialization.UnitTests
{
    internal static class CsCheckAdaptor
    {
        public static Action<Action<TValue>> ToValueProvider<TValue>(this Gen<TValue> gen) => value => gen.Sample(value);
    }

    [GenerateSerializer]
    public enum MyEnum : short
    {
        None,
        One,
        Two
    }

    public class CodecTestTests
    {
        [Fact]
        public void EveryCodecHasTests()
        {
            var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
            var serializerOptions = services.GetRequiredService<IOptions<TypeManifestOptions>>();
            var typesWithCodecs = new HashSet<Type>();
            var typesWithCopiers = new HashSet<Type>();
            foreach (var codec in serializerOptions.Value.Serializers)
            {
                if (codec.IsAbstract || codec.GetCustomAttribute<GeneratedCodeAttribute>() is not null)
                {
                    continue;
                }

                foreach (var iface in codec.GetInterfaces())
                {
                    if (!iface.IsGenericType)
                    {
                        continue;
                    }

                    var gtd = iface.GetGenericTypeDefinition();
                    if (gtd == typeof(IFieldCodec<>) && iface.GenericTypeArguments[0] is { IsArray: false })
                    {
                        var typeArg = iface.GenericTypeArguments[0] switch
                        {
                            { IsGenericType: true } gt => gt.GetGenericTypeDefinition(),
                            { } t => t,
                        };
                        typesWithCodecs.Add(typeArg);
                    }

                    if (gtd == typeof(IDeepCopier<>) && gtd.GenericTypeArguments[0] is { IsArray: false })
                    {
                        var typeArg = iface.GenericTypeArguments[0] switch
                        {
                            { IsGenericType: true } gt => gt.GetGenericTypeDefinition(),
                            { } t => t,
                        };
                        typesWithCopiers.Add(typeArg);
                    }   
                }
            }

            var typesWithCodecTests = new HashSet<Type>();
            var typesWithCopierTests = new HashSet<Type>();
            foreach (var type in typeof(CodecTestTests).Assembly.GetTypes())
            {
                if (type.BaseType is not { IsGenericType: true } baseType)
                {
                    continue;
                }

                var genericTypeDefinition = baseType.GetGenericTypeDefinition();
                if (genericTypeDefinition == typeof(FieldCodecTester<,>))
                {
                    var typeArg = baseType.GenericTypeArguments[0] switch
                    {
                        { IsGenericType: true } gt => gt.GetGenericTypeDefinition(),
                        { } t => t,
                    };

                    typesWithCodecTests.Add(typeArg);
                }
                else if (genericTypeDefinition == typeof(CopierTester<,>))
                {
                    var typeArg = baseType.GenericTypeArguments[0] switch
                    {
                        { IsGenericType: true } gt => gt.GetGenericTypeDefinition(),
                        { } t => t,
                    };

                    typesWithCopierTests.Add(typeArg);
                }
            }

            var typesWithoutCodecTests = typesWithCodecs.Except(typesWithCodecTests).ToArray();
            if (typesWithoutCodecTests.Length > 0)
            {
                Assert.Fail($"Missing codec tests for \n * {string.Join("\n * ", typesWithoutCodecTests.Select(t => t.ToString()))}");
            }

            var typesWithoutCopierTests = typesWithCopiers.Except(typesWithCopierTests).ToArray();
            if (typesWithoutCopierTests.Length > 0)
            {
                Assert.Fail($"Missing copier tests for \n * {string.Join("\n * ", typesWithoutCopierTests.Select(t => t.ToString()))}");
            }
        }
    }

    public class EnumTests(ITestOutputHelper output) : FieldCodecTester<MyEnum, IFieldCodec<MyEnum>>(output)
    {
        protected override IFieldCodec<MyEnum> CreateCodec() => ServiceProvider.GetRequiredService<ICodecProvider>().GetCodec<MyEnum>();
        protected override MyEnum CreateValue() => (MyEnum)Random.Next((int)MyEnum.None, (int)MyEnum.Two);
        protected override MyEnum[] TestValues => [MyEnum.None, MyEnum.One, MyEnum.Two, (MyEnum)(-1), (MyEnum)10_000];
        protected override void Configure(ISerializerBuilder builder)
        {
            builder.Services.RemoveAll(typeof(IFieldCodec<MyEnum>));
        }

        protected override Action<Action<MyEnum>> ValueProvider => Gen.Int.Select(v => (MyEnum)v).ToValueProvider();
    }

    public class EnumCopierTests(ITestOutputHelper output) : CopierTester<MyEnum, IDeepCopier<MyEnum>>(output)
    {
        protected override IDeepCopier<MyEnum> CreateCopier() => ServiceProvider.GetRequiredService<ICodecProvider>().GetDeepCopier<MyEnum>();
        protected override MyEnum CreateValue() => (MyEnum)Random.Next((int)MyEnum.None, (int)MyEnum.Two);
        protected override MyEnum[] TestValues => [MyEnum.None, MyEnum.One, MyEnum.Two, (MyEnum)(-1), (MyEnum)10_000];
        protected override void Configure(ISerializerBuilder builder)
        {
            builder.Services.RemoveAll(typeof(IFieldCodec<MyEnum>));
        }

        protected override Action<Action<MyEnum>> ValueProvider => Gen.Int.Select(v => (MyEnum)v).ToValueProvider();
    }

    public class DateTimeKindTests(ITestOutputHelper output) : FieldCodecTester<DateTimeKind, IFieldCodec<DateTimeKind>>(output)
    {
        protected override IFieldCodec<DateTimeKind> CreateCodec() => ServiceProvider.GetRequiredService<ICodecProvider>().GetCodec<DateTimeKind>();
        protected override DateTimeKind CreateValue() => (DateTimeKind)Random.Next((int)DateTimeKind.Unspecified, (int)DateTimeKind.Local + 1);
        protected override DateTimeKind[] TestValues => [DateTimeKind.Utc, DateTimeKind.Unspecified, (DateTimeKind)(-1), (DateTimeKind)10_000];

        protected override Action<Action<DateTimeKind>> ValueProvider => Gen.Int.Select(v => (DateTimeKind)v).ToValueProvider();
    }

    public class DateTimeKindCopierTests(ITestOutputHelper output) : CopierTester<DateTimeKind, IDeepCopier<DateTimeKind>>(output)
    {
        protected override IDeepCopier<DateTimeKind> CreateCopier() => ServiceProvider.GetRequiredService<ICodecProvider>().GetDeepCopier<DateTimeKind>();
        protected override DateTimeKind CreateValue() => (DateTimeKind)Random.Next((int)DateTimeKind.Unspecified, (int)DateTimeKind.Local + 1);
        protected override DateTimeKind[] TestValues => [DateTimeKind.Utc, DateTimeKind.Local, (DateTimeKind)(-1), (DateTimeKind)10_000];

        protected override Action<Action<DateTimeKind>> ValueProvider => Gen.Int.Select(v => (DateTimeKind)v).ToValueProvider();
    }

    public class DayOfWeekTests(ITestOutputHelper output) : FieldCodecTester<DayOfWeek, IFieldCodec<DayOfWeek>>(output)
    {
        protected override IFieldCodec<DayOfWeek> CreateCodec() => ServiceProvider.GetRequiredService<ICodecProvider>().GetCodec<DayOfWeek>();
        protected override DayOfWeek CreateValue() => (DayOfWeek)Random.Next((int)DayOfWeek.Sunday, (int)DayOfWeek.Saturday + 1);
        protected override DayOfWeek[] TestValues => [DayOfWeek.Monday, DayOfWeek.Sunday, (DayOfWeek)(-1), (DayOfWeek)10_000];

        protected override Action<Action<DayOfWeek>> ValueProvider => Gen.Int.Select(v => (DayOfWeek)v).ToValueProvider();
    }

    public class DayOfWeekCopierTests(ITestOutputHelper output) : CopierTester<DayOfWeek, IDeepCopier<DayOfWeek>>(output)
    {
        protected override IDeepCopier<DayOfWeek> CreateCopier() => ServiceProvider.GetRequiredService<ICodecProvider>().GetDeepCopier<DayOfWeek>();
        protected override DayOfWeek CreateValue() => (DayOfWeek)Random.Next((int)DayOfWeek.Sunday, (int)DayOfWeek.Saturday);
        protected override DayOfWeek[] TestValues => [DayOfWeek.Monday, DayOfWeek.Sunday, (DayOfWeek)(-1), (DayOfWeek)10_000];

        protected override Action<Action<DayOfWeek>> ValueProvider => Gen.Int.Select(v => (DayOfWeek)v).ToValueProvider();
    }

    public class NullableIntTests(ITestOutputHelper output) : FieldCodecTester<int?, IFieldCodec<int?>>(output)
    {
        protected override IFieldCodec<int?> CreateCodec() => ServiceProvider.GetRequiredService<ICodecProvider>().GetCodec<int?>();
        protected override int? CreateValue() => TestValues[Random.Next(TestValues.Length)];
        protected override int?[] TestValues => [null, 1, 2, -3];
    }

    public class NullableIntCopierTests(ITestOutputHelper output) : CopierTester<int?, IDeepCopier<int?>>(output)
    {
        protected override IDeepCopier<int?> CreateCopier() => ServiceProvider.GetRequiredService<ICodecProvider>().GetDeepCopier<int?>();
        protected override int? CreateValue() => TestValues[Random.Next(TestValues.Length)];
        protected override int?[] TestValues => [null, 1, 2, -3];
    }

    public class DateTimeTests(ITestOutputHelper output) : FieldCodecTester<DateTime, DateTimeCodec>(output)
    {
        protected override DateTime CreateValue() => DateTime.UtcNow;
        protected override DateTime[] TestValues => [DateTime.MinValue, DateTime.MaxValue, new DateTime(1970, 1, 1, 0, 0, 0)];
        protected override Action<Action<DateTime>> ValueProvider => Gen.DateTime.ToValueProvider();
    }

    public class DateTimeCopierTests(ITestOutputHelper output) : CopierTester<DateTime, IDeepCopier<DateTime>>(output)
    {
        protected override DateTime CreateValue() => DateTime.UtcNow;
        protected override DateTime[] TestValues => [DateTime.MinValue, DateTime.MaxValue, new DateTime(1970, 1, 1, 0, 0, 0)];
        protected override Action<Action<DateTime>> ValueProvider => Gen.DateTime.ToValueProvider();
    }

#if NET6_0_OR_GREATER
    public class DateOnlyTests(ITestOutputHelper output) : FieldCodecTester<DateOnly, DateOnlyCodec>(output)
    {
        protected override DateOnly CreateValue() => DateOnly.FromDateTime(DateTime.UtcNow);
        protected override DateOnly[] TestValues => [DateOnly.MinValue, DateOnly.MaxValue, new DateOnly(1970, 1, 1), CreateValue()];
        protected override Action<Action<DateOnly>> ValueProvider => assert => Gen.Date.Sample(dt => assert(DateOnly.FromDateTime(dt)));
    }

    public class TimeOnlyTests(ITestOutputHelper output) : FieldCodecTester<TimeOnly, TimeOnlyCodec>(output)
    {
        protected override TimeOnly CreateValue() => TimeOnly.FromDateTime(DateTime.UtcNow);
        protected override TimeOnly[] TestValues => [TimeOnly.MinValue, TimeOnly.MaxValue, TimeOnly.FromTimeSpan(TimeSpan.Zero), CreateValue()];
        protected override Action<Action<TimeOnly>> ValueProvider => assert => Gen.Date.Sample(dt => assert(TimeOnly.FromDateTime(dt)));
    }

    public class DateOnlyCopierTests(ITestOutputHelper output) : CopierTester<DateOnly, IDeepCopier<DateOnly>>(output)
    {
        protected override DateOnly CreateValue() => DateOnly.FromDateTime(DateTime.UtcNow);
        protected override DateOnly[] TestValues => [DateOnly.MinValue, DateOnly.MaxValue, new DateOnly(1970, 1, 1), CreateValue()];
        protected override Action<Action<DateOnly>> ValueProvider => assert => Gen.Date.Sample(dt => assert(DateOnly.FromDateTime(dt)));
    }

    public class TimeOnlyCopierTests(ITestOutputHelper output) : CopierTester<TimeOnly, IDeepCopier<TimeOnly>>(output)
    {
        protected override TimeOnly CreateValue() => TimeOnly.FromDateTime(DateTime.UtcNow);
        protected override TimeOnly[] TestValues => [TimeOnly.MinValue, TimeOnly.MaxValue, TimeOnly.FromTimeSpan(TimeSpan.Zero), CreateValue()];
        protected override Action<Action<TimeOnly>> ValueProvider => assert => Gen.Date.Sample(dt => assert(TimeOnly.FromDateTime(dt)));
    }
#endif

    public class TimeSpanTests(ITestOutputHelper output) : FieldCodecTester<TimeSpan, TimeSpanCodec>(output)
    {
        protected override TimeSpan CreateValue() => TimeSpan.FromMilliseconds(Guid.NewGuid().GetHashCode());
        protected override TimeSpan[] TestValues => [TimeSpan.MinValue, TimeSpan.MaxValue, TimeSpan.Zero, TimeSpan.FromSeconds(12345)];
        protected override Action<Action<TimeSpan>> ValueProvider => Gen.TimeSpan.ToValueProvider();
    }

    public class TimeSpanCopierTests(ITestOutputHelper output) : CopierTester<TimeSpan, IDeepCopier<TimeSpan>>(output)
    {
        protected override TimeSpan CreateValue() => TimeSpan.FromMilliseconds(Guid.NewGuid().GetHashCode());
        protected override TimeSpan[] TestValues => [TimeSpan.MinValue, TimeSpan.MaxValue, TimeSpan.Zero, TimeSpan.FromSeconds(12345)];
        protected override Action<Action<TimeSpan>> ValueProvider => Gen.TimeSpan.ToValueProvider();
    }

    public class DateTimeOffsetTests(ITestOutputHelper output) : FieldCodecTester<DateTimeOffset, DateTimeOffsetCodec>(output)
    {
        protected override DateTimeOffset CreateValue() => DateTime.UtcNow;
        protected override DateTimeOffset[] TestValues =>
        [
            DateTimeOffset.MinValue,
            DateTimeOffset.MaxValue,
            new DateTimeOffset(new DateTime(1970, 1, 1, 0, 0, 0), TimeSpan.FromHours(11.5)),
            new DateTimeOffset(new DateTime(1970, 1, 1, 0, 0, 0), TimeSpan.FromHours(-11.5)),
        ];

        protected override Action<Action<DateTimeOffset>> ValueProvider => Gen.DateTimeOffset.ToValueProvider();
    }

    public class DateTimeOffsetCopierTests(ITestOutputHelper output) : CopierTester<DateTimeOffset, IDeepCopier<DateTimeOffset>>(output)
    {
        protected override DateTimeOffset CreateValue() => DateTime.UtcNow;
        protected override DateTimeOffset[] TestValues =>
        [
            DateTimeOffset.MinValue,
            DateTimeOffset.MaxValue,
            new DateTimeOffset(new DateTime(1970, 1, 1, 0, 0, 0), TimeSpan.FromHours(11.5)),
            new DateTimeOffset(new DateTime(1970, 1, 1, 0, 0, 0), TimeSpan.FromHours(-11.5)),
        ];

        protected override Action<Action<DateTimeOffset>> ValueProvider => Gen.DateTimeOffset.ToValueProvider();
    }

    public class VersionTests(ITestOutputHelper output) : FieldCodecTester<Version, VersionCodec>(output)
    {
        protected override Version CreateValue() => new();
        protected override Version[] TestValues =>
        [
            new Version(),
            new Version(1, 2),
            new Version(1, 2, 3),
            new Version(1, 2, 3, 4),
            new Version("1.2"),
            new Version("1.2.3"),
            new Version("1.2.3.4")
        ];

        protected override bool Equals(Version left, Version right) => left == right && (left is null || left.GetHashCode() == right.GetHashCode());
    }

    public class VersionCopierTests(ITestOutputHelper output) : CopierTester<Version, IDeepCopier<Version>>(output)
    {
        protected override Version CreateValue() => new();
        protected override Version[] TestValues =>
        [
            new Version(),
            new Version(1, 2),
            new Version(1, 2, 3),
            new Version(1, 2, 3, 4),
            new Version("1.2"),
            new Version("1.2.3"),
            new Version("1.2.3.4")
        ];

        protected override bool Equals(Version left, Version right) => left == right && (left is null || left.GetHashCode() == right.GetHashCode());

        protected override bool IsImmutable => true;
    }

    public class BitVector32Tests(ITestOutputHelper output) : FieldCodecTester<BitVector32, BitVector32Codec>(output)
    {
        protected override BitVector32 CreateValue() => new(Random.Next());

        protected override BitVector32[] TestValues =>
        [
            new BitVector32(0),
            new BitVector32(100),
            new BitVector32(-100),
            CreateValue(),
            CreateValue(),
            CreateValue()
        ];

        protected override bool Equals(BitVector32 left, BitVector32 right) => left.Equals(right) && left.GetHashCode() == right.GetHashCode();
    }

    public class BitVector32CopierTests(ITestOutputHelper output) : CopierTester<BitVector32, IDeepCopier<BitVector32>>(output)
    {
        protected override BitVector32 CreateValue() => new(Random.Next());

        protected override BitVector32[] TestValues =>
        [
            new BitVector32(0),
            new BitVector32(100),
            new BitVector32(-100),
            CreateValue(),
            CreateValue(),
            CreateValue()
        ];

        protected override bool Equals(BitVector32 left, BitVector32 right) => left.Equals(right) && left.GetHashCode() == right.GetHashCode();
    }

    public class KeyValuePairTests(ITestOutputHelper output) : FieldCodecTester<KeyValuePair<string, string>, KeyValuePairCodec<string, string>>(output)
    {
        protected override KeyValuePair<string, string> CreateValue() => KeyValuePair.Create(Guid.NewGuid().ToString(), Guid.NewGuid().ToString());

        protected override KeyValuePair<string, string>[] TestValues =>
        [
            default,
            KeyValuePair.Create<string, string>(null, null),
            KeyValuePair.Create<string, string>(string.Empty, "foo"),
            KeyValuePair.Create<string, string>("foo", "bar"),
            KeyValuePair.Create<string, string>("foo", "foo"),
        ];
    }

    public class KeyValuePairCopierTests(ITestOutputHelper output) : CopierTester<KeyValuePair<string, string>, KeyValuePairCopier<string, string>>(output)
    {
        protected override KeyValuePair<string, string> CreateValue() => KeyValuePair.Create(Guid.NewGuid().ToString(), Guid.NewGuid().ToString());

        protected override KeyValuePair<string, string>[] TestValues =>
        [
            default,
            KeyValuePair.Create<string, string>(null, null),
            KeyValuePair.Create<string, string>(string.Empty, "foo"),
            KeyValuePair.Create<string, string>("foo", "bar"),
            KeyValuePair.Create<string, string>("foo", "foo"),
        ];
    }

    public class Tuple1Tests(ITestOutputHelper output) : FieldCodecTester<Tuple<string>, TupleCodec<string>>(output)
    {
        protected override Tuple<string> CreateValue() => Tuple.Create(Guid.NewGuid().ToString());

        protected override Tuple<string>[] TestValues =>
        [
            null,
            Tuple.Create<string>(null),
            Tuple.Create<string>(string.Empty),
            Tuple.Create<string>("foobar")
        ];
    }

    public class Tuple1CopierTests(ITestOutputHelper output) : CopierTester<Tuple<string>, TupleCopier<string>>(output)
    {
        protected override Tuple<string> CreateValue() => Tuple.Create(Guid.NewGuid().ToString());

        protected override Tuple<string>[] TestValues =>
        [
            null,
            Tuple.Create<string>(null),
            Tuple.Create<string>(string.Empty),
            Tuple.Create<string>("foobar")
        ];

        protected override bool IsImmutable => true;
    }

    public class Tuple2Tests(ITestOutputHelper output) : FieldCodecTester<Tuple<string, string>, TupleCodec<string, string>>(output)
    {
        protected override Tuple<string, string> CreateValue() => Tuple.Create(Guid.NewGuid().ToString(), Guid.NewGuid().ToString());

        protected override Tuple<string, string>[] TestValues =>
        [
            null,
            Tuple.Create<string, string>(null, null),
            Tuple.Create<string, string>(string.Empty, "foo"),
            Tuple.Create<string, string>("foo", "bar"),
            Tuple.Create<string, string>("foo", "foo"),
        ];
    }

    public class Tuple2CopierTests(ITestOutputHelper output) : CopierTester<Tuple<string, string>, TupleCopier<string, string>>(output)
    {
        protected override Tuple<string, string> CreateValue() => Tuple.Create(Guid.NewGuid().ToString(), Guid.NewGuid().ToString());

        protected override Tuple<string, string>[] TestValues =>
        [
            null,
            Tuple.Create<string, string>(null, null),
            Tuple.Create<string, string>(string.Empty, "foo"),
            Tuple.Create<string, string>("foo", "bar"),
            Tuple.Create<string, string>("foo", "foo"),
        ];

        protected override bool IsImmutable => true;
    }

    public class Tuple3Tests(ITestOutputHelper output) : FieldCodecTester<Tuple<string, string, string>, TupleCodec<string, string, string>>(output)
    {
        protected override Tuple<string, string, string> CreateValue() => Tuple.Create(
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString());

        protected override Tuple<string, string, string>[] TestValues =>
        [
            null,
            Tuple.Create(default(string), default(string), default(string)),
            Tuple.Create(string.Empty, string.Empty, "foo"),
            Tuple.Create("foo", "bar", "baz"),
            Tuple.Create("foo", "foo", "foo")
        ];
    }

    public class Tuple3CopierTests(ITestOutputHelper output) : CopierTester<Tuple<string, string, string>, TupleCopier<string, string, string>>(output)
    {
        protected override Tuple<string, string, string> CreateValue() => Tuple.Create(
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString());

        protected override Tuple<string, string, string>[] TestValues =>
        [
            null,
            Tuple.Create(default(string), default(string), default(string)),
            Tuple.Create(string.Empty, string.Empty, "foo"),
            Tuple.Create("foo", "bar", "baz"),
            Tuple.Create("foo", "foo", "foo")
        ];

        protected override bool IsImmutable => true;
    }

    public class Tuple4Tests(ITestOutputHelper output) : FieldCodecTester<Tuple<string, string, string, string>, TupleCodec<string, string, string, string>>(output)
    {
        protected override Tuple<string, string, string, string> CreateValue() => Tuple.Create(
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString());

        protected override Tuple<string, string, string, string>[] TestValues =>
        [
            null,
            Tuple.Create(default(string), default(string), default(string), default(string)),
            Tuple.Create(string.Empty, string.Empty, string.Empty, "foo"),
            Tuple.Create("foo", "bar", "baz", "4"),
            Tuple.Create("foo", "foo", "foo", "foo")
        ];
    }

    public class Tuple4CopierTests(ITestOutputHelper output) : CopierTester<Tuple<string, string, string, string>, TupleCopier<string, string, string, string>>(output)
    {
        protected override Tuple<string, string, string, string> CreateValue() => Tuple.Create(
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString());

        protected override Tuple<string, string, string, string>[] TestValues =>
        [
            null,
            Tuple.Create(default(string), default(string), default(string), default(string)),
            Tuple.Create(string.Empty, string.Empty, string.Empty, "foo"),
            Tuple.Create("foo", "bar", "baz", "4"),
            Tuple.Create("foo", "foo", "foo", "foo")
        ];

        protected override bool IsImmutable => true;
    }

    public class Tuple5Tests(ITestOutputHelper output) : FieldCodecTester<Tuple<string, string, string, string, string>, TupleCodec<string, string, string, string, string>>(output)
    {
        protected override Tuple<string, string, string, string, string> CreateValue() => Tuple.Create(
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString());

        protected override Tuple<string, string, string, string, string>[] TestValues =>
        [
            null,
            Tuple.Create(default(string), default(string), default(string), default(string), default(string)),
            Tuple.Create(string.Empty, string.Empty, string.Empty,string.Empty, "foo"),
            Tuple.Create("foo", "bar", "baz", "4", "5"),
            Tuple.Create("foo", "foo", "foo", "foo", "foo")
        ];
    }

    public class Tuple5CopierTests(ITestOutputHelper output) : CopierTester<Tuple<string, string, string, string, string>, TupleCopier<string, string, string, string, string>>(output)
    {
        protected override Tuple<string, string, string, string, string> CreateValue() => Tuple.Create(
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString());

        protected override Tuple<string, string, string, string, string>[] TestValues =>
        [
            null,
            Tuple.Create(default(string), default(string), default(string), default(string), default(string)),
            Tuple.Create(string.Empty, string.Empty, string.Empty,string.Empty, "foo"),
            Tuple.Create("foo", "bar", "baz", "4", "5"),
            Tuple.Create("foo", "foo", "foo", "foo", "foo")
        ];

        protected override bool IsImmutable => true;
    }

    public class Tuple6Tests(ITestOutputHelper output) : FieldCodecTester<Tuple<string, string,string, string, string, string>, TupleCodec<string, string, string, string, string, string>>(output)
    {
        protected override Tuple<string, string, string, string, string, string> CreateValue() => Tuple.Create(
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString());

        protected override Tuple<string, string, string, string, string, string>[] TestValues =>
        [
            null,
            Tuple.Create(default(string), default(string), default(string), default(string), default(string), default(string)),
            Tuple.Create(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, "foo"),
            Tuple.Create("foo", "bar", "baz", "4", "5", "6"),
            Tuple.Create("foo", "foo", "foo", "foo", "foo", "foo")
        ];
    }

    public class Tuple6CopierTests(ITestOutputHelper output) : CopierTester<Tuple<string, string,string, string, string, string>, TupleCopier<string, string, string, string, string, string>>(output)
    {
        protected override Tuple<string, string, string, string, string, string> CreateValue() => Tuple.Create(
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString());

        protected override Tuple<string, string, string, string, string, string>[] TestValues =>
        [
            null,
            Tuple.Create(default(string), default(string), default(string), default(string), default(string), default(string)),
            Tuple.Create(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, "foo"),
            Tuple.Create("foo", "bar", "baz", "4", "5", "6"),
            Tuple.Create("foo", "foo", "foo", "foo", "foo", "foo")
        ];

        protected override bool IsImmutable => true;
    }

    public class Tuple7Tests(ITestOutputHelper output) : FieldCodecTester<Tuple<string, string, string, string, string, string, string>, TupleCodec<string, string, string, string, string, string, string>>(output)
    {
        protected override Tuple<string, string, string, string, string, string, string> CreateValue() => Tuple.Create(
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString());

        protected override Tuple<string, string, string, string, string, string, string>[] TestValues =>
        [
            null,
            Tuple.Create(default(string), default(string), default(string), default(string), default(string), default(string), default(string)),
            Tuple.Create(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, "foo"),
            Tuple.Create("foo", "bar", "baz", "4", "5", "6", "7"),
            Tuple.Create("foo", "foo", "foo", "foo", "foo", "foo", "foo")
        ];
    }

    public class Tuple7CopierTests(ITestOutputHelper output) : CopierTester<Tuple<string, string, string, string, string, string, string>, TupleCopier<string, string, string, string, string, string, string>>(output)
    {
        protected override Tuple<string, string, string, string, string, string, string> CreateValue() => Tuple.Create(
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString());

        protected override Tuple<string, string, string, string, string, string, string>[] TestValues =>
        [
            null,
            Tuple.Create(default(string), default(string), default(string), default(string), default(string), default(string), default(string)),
            Tuple.Create(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, "foo"),
            Tuple.Create("foo", "bar", "baz", "4", "5", "6", "7"),
            Tuple.Create("foo", "foo", "foo", "foo", "foo", "foo", "foo")
        ];

        protected override bool IsImmutable => true;
    }

    public class Tuple8Tests(ITestOutputHelper output) : FieldCodecTester<Tuple<string, string, string, string, string, string, string, Tuple<string>>, TupleCodec<string, string, string, string, string, string, string, Tuple<string>>>(output)
    {
        protected override Tuple<string, string, string, string, string, string, string, Tuple<string>> CreateValue() => new(
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            new Tuple<string>(Guid.NewGuid().ToString()));

        protected override Tuple<string, string, string, string, string, string, string, Tuple<string>>[] TestValues =>
        [
            null,
            new Tuple<string, string, string, string, string, string, string, Tuple<string>>(default, default, default, default, default, default, default, new Tuple<string>(default)),
            new Tuple<string, string, string, string, string, string, string, Tuple<string>>(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, "foo", Tuple.Create("foo")),
            new Tuple<string, string, string, string, string, string, string, Tuple<string>>("foo", "bar", "baz", "4", "5", "6", "7", Tuple.Create("8")),
            new Tuple<string, string, string, string, string, string, string, Tuple<string>>("foo", "foo", "foo", "foo", "foo", "foo", "foo", Tuple.Create("foo"))
        ];
    }

    public class Tuple8CopierTests(ITestOutputHelper output) : CopierTester<Tuple<string, string, string, string, string, string, string, Tuple<string>>, TupleCopier<string, string, string, string, string, string, string, Tuple<string>>>(output)
    {
        protected override Tuple<string, string, string, string, string, string, string, Tuple<string>> CreateValue() => new(
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            new Tuple<string>(Guid.NewGuid().ToString()));

        protected override Tuple<string, string, string, string, string, string, string, Tuple<string>>[] TestValues =>
        [
            null,
            new Tuple<string, string, string, string, string, string, string, Tuple<string>>(default, default, default, default, default, default, default, new Tuple<string>(default)),
            new Tuple<string, string, string, string, string, string, string, Tuple<string>>(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, "foo", Tuple.Create("foo")),
            new Tuple<string, string, string, string, string, string, string, Tuple<string>>("foo", "bar", "baz", "4", "5", "6", "7", Tuple.Create("8")),
            new Tuple<string, string, string, string, string, string, string, Tuple<string>>("foo", "foo", "foo", "foo", "foo", "foo", "foo", Tuple.Create("foo"))
        ];

        protected override bool IsImmutable => true;
    }

    public class ValueTupleTests(ITestOutputHelper output) : FieldCodecTester<ValueTuple, ValueTupleCodec>(output)
    {
        protected override ValueTuple CreateValue() => default;

        protected override ValueTuple[] TestValues => [ default ];
    }

    public class ValueTupleCopierTests(ITestOutputHelper output) : CopierTester<ValueTuple, ValueTupleCopier>(output)
    {
        protected override ValueTuple CreateValue() => default;

        protected override ValueTuple[] TestValues => [ default ];
    }

    public class ValueTuple1Tests(ITestOutputHelper output) : FieldCodecTester<ValueTuple<string>, ValueTupleCodec<string>>(output)
    {
        protected override ValueTuple<string> CreateValue() => ValueTuple.Create(Guid.NewGuid().ToString());

        protected override ValueTuple<string>[] TestValues =>
        [
            default,
            ValueTuple.Create<string>(null),
            ValueTuple.Create<string>(string.Empty),
            ValueTuple.Create<string>("foobar")
        ];
    }

    public class ValueTuple1CopierTests(ITestOutputHelper output) : CopierTester<ValueTuple<string>, ValueTupleCopier<string>>(output)
    {
        protected override ValueTuple<string> CreateValue() => ValueTuple.Create(Guid.NewGuid().ToString());

        protected override ValueTuple<string>[] TestValues =>
        [
            default,
            ValueTuple.Create<string>(null),
            ValueTuple.Create<string>(string.Empty),
            ValueTuple.Create<string>("foobar")
        ];
    }

    public class ValueTuple2Tests(ITestOutputHelper output) : FieldCodecTester<ValueTuple<string, string>, ValueTupleCodec<string, string>>(output)
    {
        protected override ValueTuple<string, string> CreateValue() => ValueTuple.Create(Guid.NewGuid().ToString(), Guid.NewGuid().ToString());

        protected override ValueTuple<string, string>[] TestValues =>
        [
            default,
            ValueTuple.Create<string, string>(null, null),
            ValueTuple.Create<string, string>(string.Empty, "foo"),
            ValueTuple.Create<string, string>("foo", "bar"),
            ValueTuple.Create<string, string>("foo", "foo"),
        ];
    }

    public class ValueTuple2CopierTests(ITestOutputHelper output) : CopierTester<ValueTuple<string, string>, ValueTupleCopier<string, string>>(output)
    {
        protected override ValueTuple<string, string> CreateValue() => ValueTuple.Create(Guid.NewGuid().ToString(), Guid.NewGuid().ToString());

        protected override ValueTuple<string, string>[] TestValues =>
        [
            default,
            ValueTuple.Create<string, string>(null, null),
            ValueTuple.Create<string, string>(string.Empty, "foo"),
            ValueTuple.Create<string, string>("foo", "bar"),
            ValueTuple.Create<string, string>("foo", "foo"),
        ];
    }

    public class ValueTuple3Tests(ITestOutputHelper output) : FieldCodecTester<ValueTuple<string, string, string>, ValueTupleCodec<string, string, string>>(output)
    {
        protected override ValueTuple<string, string, string> CreateValue() => ValueTuple.Create(
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString());

        protected override ValueTuple<string, string, string>[] TestValues =>
        [
            default,
            ValueTuple.Create(default(string), default(string), default(string)),
            ValueTuple.Create(string.Empty, string.Empty, "foo"),
            ValueTuple.Create("foo", "bar", "baz"),
            ValueTuple.Create("foo", "foo", "foo")
        ];
    }

    public class ValueTuple3CopierTests(ITestOutputHelper output) : CopierTester<ValueTuple<string, string, string>, ValueTupleCopier<string, string, string>>(output)
    {
        protected override ValueTuple<string, string, string> CreateValue() => ValueTuple.Create(
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString());

        protected override ValueTuple<string, string, string>[] TestValues =>
        [
            default,
            ValueTuple.Create(default(string), default(string), default(string)),
            ValueTuple.Create(string.Empty, string.Empty, "foo"),
            ValueTuple.Create("foo", "bar", "baz"),
            ValueTuple.Create("foo", "foo", "foo")
        ];
    }

    public class ValueTuple4Tests(ITestOutputHelper output) : FieldCodecTester<ValueTuple<string, string, string, string>, ValueTupleCodec<string, string, string, string>>(output)
    {
        protected override ValueTuple<string, string, string, string> CreateValue() => ValueTuple.Create(
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString());

        protected override ValueTuple<string, string, string, string>[] TestValues =>
        [
            default,
            ValueTuple.Create(default(string), default(string), default(string), default(string)),
            ValueTuple.Create(string.Empty, string.Empty, string.Empty, "foo"),
            ValueTuple.Create("foo", "bar", "baz", "4"),
            ValueTuple.Create("foo", "foo", "foo", "foo")
        ];
    }

    public class ValueTuple4CopierTests(ITestOutputHelper output) : CopierTester<ValueTuple<string, string, string, string>, ValueTupleCopier<string, string, string, string>>(output)
    {
        protected override ValueTuple<string, string, string, string> CreateValue() => ValueTuple.Create(
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString());

        protected override ValueTuple<string, string, string, string>[] TestValues =>
        [
            default,
            ValueTuple.Create(default(string), default(string), default(string), default(string)),
            ValueTuple.Create(string.Empty, string.Empty, string.Empty, "foo"),
            ValueTuple.Create("foo", "bar", "baz", "4"),
            ValueTuple.Create("foo", "foo", "foo", "foo")
        ];
    }

    public class ValueTuple5Tests(ITestOutputHelper output) : FieldCodecTester<ValueTuple<string, string, string, string, string>, ValueTupleCodec<string, string, string, string, string>>(output)
    {
        protected override ValueTuple<string, string, string, string, string> CreateValue() => ValueTuple.Create(
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString());

        protected override ValueTuple<string, string, string, string, string>[] TestValues =>
        [
            default,
            ValueTuple.Create(default(string), default(string), default(string), default(string), default(string)),
            ValueTuple.Create(string.Empty, string.Empty, string.Empty,string.Empty, "foo"),
            ValueTuple.Create("foo", "bar", "baz", "4", "5"),
            ValueTuple.Create("foo", "foo", "foo", "foo", "foo")
        ];
    }

    public class ValueTuple5CopierTests(ITestOutputHelper output) : CopierTester<ValueTuple<string, string, string, string, string>, ValueTupleCopier<string, string, string, string, string>>(output)
    {
        protected override ValueTuple<string, string, string, string, string> CreateValue() => ValueTuple.Create(
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString());

        protected override ValueTuple<string, string, string, string, string>[] TestValues =>
        [
            default,
            ValueTuple.Create(default(string), default(string), default(string), default(string), default(string)),
            ValueTuple.Create(string.Empty, string.Empty, string.Empty,string.Empty, "foo"),
            ValueTuple.Create("foo", "bar", "baz", "4", "5"),
            ValueTuple.Create("foo", "foo", "foo", "foo", "foo")
        ];
    }

    public class ValueTuple6Tests(ITestOutputHelper output) : FieldCodecTester<ValueTuple<string, string,string, string, string, string>, ValueTupleCodec<string, string, string, string, string, string>>(output)
    {
        protected override ValueTuple<string, string, string, string, string, string> CreateValue() => ValueTuple.Create(
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString());

        protected override ValueTuple<string, string, string, string, string, string>[] TestValues =>
        [
            default,
            ValueTuple.Create(default(string), default(string), default(string), default(string), default(string), default(string)),
            ValueTuple.Create(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, "foo"),
            ValueTuple.Create("foo", "bar", "baz", "4", "5", "6"),
            ValueTuple.Create("foo", "foo", "foo", "foo", "foo", "foo")
        ];
    }

    public class ValueTuple6CopierTests(ITestOutputHelper output) : CopierTester<ValueTuple<string, string,string, string, string, string>, ValueTupleCopier<string, string, string, string, string, string>>(output)
    {
        protected override ValueTuple<string, string, string, string, string, string> CreateValue() => ValueTuple.Create(
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString());

        protected override ValueTuple<string, string, string, string, string, string>[] TestValues =>
        [
            default,
            ValueTuple.Create(default(string), default(string), default(string), default(string), default(string), default(string)),
            ValueTuple.Create(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, "foo"),
            ValueTuple.Create("foo", "bar", "baz", "4", "5", "6"),
            ValueTuple.Create("foo", "foo", "foo", "foo", "foo", "foo")
        ];
    }

    public class ValueTuple7Tests(ITestOutputHelper output) : FieldCodecTester<ValueTuple<string, string, string, string, string, string, string>, ValueTupleCodec<string, string, string, string, string, string, string>>(output)
    {
        protected override ValueTuple<string, string, string, string, string, string, string> CreateValue() => ValueTuple.Create(
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString());

        protected override ValueTuple<string, string, string, string, string, string, string>[] TestValues =>
        [
            default,
            ValueTuple.Create(default(string), default(string), default(string), default(string), default(string), default(string), default(string)),
            ValueTuple.Create(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, "foo"),
            ValueTuple.Create("foo", "bar", "baz", "4", "5", "6", "7"),
            ValueTuple.Create("foo", "foo", "foo", "foo", "foo", "foo", "foo")
        ];
    }

    public class ValueTuple7opierTests(ITestOutputHelper output) : CopierTester<ValueTuple<string, string, string, string, string, string, string>, ValueTupleCopier<string, string, string, string, string, string, string>>(output)
    {
        protected override ValueTuple<string, string, string, string, string, string, string> CreateValue() => ValueTuple.Create(
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString());

        protected override ValueTuple<string, string, string, string, string, string, string>[] TestValues =>
        [
            default,
            ValueTuple.Create(default(string), default(string), default(string), default(string), default(string), default(string), default(string)),
            ValueTuple.Create(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, "foo"),
            ValueTuple.Create("foo", "bar", "baz", "4", "5", "6", "7"),
            ValueTuple.Create("foo", "foo", "foo", "foo", "foo", "foo", "foo")
        ];
    }

    public class ValueTuple8Tests(ITestOutputHelper output) : FieldCodecTester<ValueTuple<string, string, string, string, string, string, string, ValueTuple<string>>, ValueTupleCodec<string, string, string, string, string, string, string, ValueTuple<string>>>(output)
    {
        protected override ValueTuple<string, string, string, string, string, string, string, ValueTuple<string>> CreateValue() => new(
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            ValueTuple.Create(Guid.NewGuid().ToString()));

        protected override ValueTuple<string, string, string, string, string, string, string, ValueTuple<string>>[] TestValues =>
        [
            default,
            new ValueTuple<string, string, string, string, string, string, string, ValueTuple<string>>(default, default, default, default, default, default, default, ValueTuple.Create(default(string))),
            new ValueTuple<string, string, string, string, string, string, string, ValueTuple<string>>(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, "foo", ValueTuple.Create("foo")),
            new ValueTuple<string, string, string, string, string, string, string, ValueTuple<string>>("foo", "bar", "baz", "4", "5", "6", "7", ValueTuple.Create("8")),
            new ValueTuple<string, string, string, string, string, string, string, ValueTuple<string>>("foo", "foo", "foo", "foo", "foo", "foo", "foo", ValueTuple.Create("foo"))
        ];
    }

    public class ValueTuple8CopierTests(ITestOutputHelper output) : CopierTester<ValueTuple<string, string, string, string, string, string, string, ValueTuple<string>>, ValueTupleCopier<string, string, string, string, string, string, string, ValueTuple<string>>>(output)
    {
        protected override ValueTuple<string, string, string, string, string, string, string, ValueTuple<string>> CreateValue() => new(
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            ValueTuple.Create(Guid.NewGuid().ToString()));

        protected override ValueTuple<string, string, string, string, string, string, string, ValueTuple<string>>[] TestValues =>
        [
            default,
            new ValueTuple<string, string, string, string, string, string, string, ValueTuple<string>>(default, default, default, default, default, default, default, ValueTuple.Create(default(string))),
            new ValueTuple<string, string, string, string, string, string, string, ValueTuple<string>>(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, "foo", ValueTuple.Create("foo")),
            new ValueTuple<string, string, string, string, string, string, string, ValueTuple<string>>("foo", "bar", "baz", "4", "5", "6", "7", ValueTuple.Create("8")),
            new ValueTuple<string, string, string, string, string, string, string, ValueTuple<string>>("foo", "foo", "foo", "foo", "foo", "foo", "foo", ValueTuple.Create("foo"))
        ];
    }

    public class BoolCodecTests(ITestOutputHelper output) : FieldCodecTester<bool, BoolCodec>(output)
    {
        protected override bool CreateValue() => true;
        protected override bool Equals(bool left, bool right) => left == right;
        protected override bool[] TestValues => [false, true];
    }

    public class BoolCopierTests(ITestOutputHelper output) : CopierTester<bool, IDeepCopier<bool>>(output)
    {
        protected override bool CreateValue() => true;
        protected override bool Equals(bool left, bool right) => left == right;
        protected override bool[] TestValues => [false, true];
    }

    public class StringCodecTests(ITestOutputHelper output) : FieldCodecTester<string, StringCodec>(output)
    {
        protected override string CreateValue() => Guid.NewGuid().ToString();
        protected override bool Equals(string left, string right) => StringComparer.Ordinal.Equals(left, right);
        protected override string[] TestValues => [null, string.Empty, new string('*', 6), new string('x', 4097), "Hello, World!"];
    }

    public class StringCopierTests(ITestOutputHelper output) : CopierTester<string, IDeepCopier<string>>(output)
    {
        protected override string CreateValue() => Guid.NewGuid().ToString();
        protected override bool Equals(string left, string right) => StringComparer.Ordinal.Equals(left, right);
        protected override string[] TestValues => [null, string.Empty, new string('*', 6), new string('x', 4097), "Hello, World!"];

        protected override bool IsImmutable => true;
    }

    public class ObjectCodecTests(ITestOutputHelper output) : FieldCodecTester<object, ObjectCodec>(output)
    {
        protected override object CreateValue() => new();
        protected override bool Equals(object left, object right) => ReferenceEquals(left, right) || typeof(object) == left?.GetType() && typeof(object) == right?.GetType();
        protected override object[] TestValues => [null, new object()];
    }

    public class ObjectCopierTests(ITestOutputHelper output) : CopierTester<object, ObjectCopier>(output)
    {
        protected override object CreateValue() => new();
        protected override bool Equals(object left, object right) => ReferenceEquals(left, right) || typeof(object) == left?.GetType() && typeof(object) == right?.GetType();
        protected override object[] TestValues => [null, new object()];
        protected override bool IsImmutable => true;
    }

    public class ByteArrayCodecTests(ITestOutputHelper output) : FieldCodecTester<byte[], ByteArrayCodec>(output)
    {
        protected override byte[] CreateValue() => Guid.NewGuid().ToByteArray();

        protected override bool Equals(byte[] left, byte[] right) => ReferenceEquals(left, right) || left.SequenceEqual(right);

        protected override byte[][] TestValues =>
        [
            null,
            Array.Empty<byte>(),
            Enumerable.Range(0, 4097).Select(b => unchecked((byte)b)).ToArray(), CreateValue(),
        ];
    }

    public class ByteArrayCopierTests(ITestOutputHelper output) : CopierTester<byte[], ByteArrayCopier>(output)
    {
        protected override byte[] CreateValue() => Guid.NewGuid().ToByteArray();

        protected override bool Equals(byte[] left, byte[] right) => ReferenceEquals(left, right) || left.SequenceEqual(right);

        protected override byte[][] TestValues =>
        [
            null,
            Array.Empty<byte>(),
            Enumerable.Range(0, 4097).Select(b => unchecked((byte)b)).ToArray(), CreateValue(),
        ];
    }

    public class MemoryCodecTests(ITestOutputHelper output) : FieldCodecTester<Memory<int>, MemoryCodec<int>>(output)
    {
        protected override Memory<int> CreateValue()
        {
            var array = Enumerable.Range(0, Random.Next(120) + 50).Select(_ => Guid.NewGuid().GetHashCode()).ToArray();
            var start = Random.Next(array.Length);
            var len = Random.Next(array.Length - start);
            return new Memory<int>(array, start, len);
        }

        protected override bool Equals(Memory<int> left, Memory<int> right) => left.Span.SequenceEqual(right.Span);
        protected override Memory<int>[] TestValues => [null, new Memory<int>([], 0, 0), CreateValue(), CreateValue(), CreateValue()];
    }

    public class MemoryCopierTests(ITestOutputHelper output) : CopierTester<Memory<int>, MemoryCopier<int>>(output)
    {
        protected override Memory<int> CreateValue()
        {
            var array = Enumerable.Range(0, Random.Next(120) + 50).Select(_ => Guid.NewGuid().GetHashCode()).ToArray();
            var start = Random.Next(array.Length);
            var len = Random.Next(array.Length - start);
            return new Memory<int>(array, start, len);
        }

        protected override bool Equals(Memory<int> left, Memory<int> right) => left.Span.SequenceEqual(right.Span);
        protected override Memory<int>[] TestValues => [null, new Memory<int>([], 0, 0), CreateValue(), CreateValue(), CreateValue()];
    }

    public class ReadOnlyMemoryCodecTests(ITestOutputHelper output) : FieldCodecTester<ReadOnlyMemory<int>, ReadOnlyMemoryCodec<int>>(output)
    {
        protected override ReadOnlyMemory<int> CreateValue()
        {
            var array = Enumerable.Range(0, Random.Next(120) + 50).Select(_ => Guid.NewGuid().GetHashCode()).ToArray();
            var start = Random.Next(array.Length);
            var len = Random.Next(array.Length - start);
            return new ReadOnlyMemory<int>(array, start, len);
        }

        protected override bool Equals(ReadOnlyMemory<int> left, ReadOnlyMemory<int> right) => left.Span.SequenceEqual(right.Span);
        protected override ReadOnlyMemory<int>[] TestValues => [null, new ReadOnlyMemory<int>([], 0, 0), CreateValue(), CreateValue(), CreateValue()];
    }

    public class ReadOnlyMemoryCopierTests(ITestOutputHelper output) : CopierTester<ReadOnlyMemory<int>, ReadOnlyMemoryCopier<int>>(output)
    {
        protected override ReadOnlyMemory<int> CreateValue()
        {
            var array = Enumerable.Range(0, Random.Next(120) + 50).Select(_ => Guid.NewGuid().GetHashCode()).ToArray();
            var start = Random.Next(array.Length);
            var len = Random.Next(array.Length - start);
            return new ReadOnlyMemory<int>(array, start, len);
        }

        protected override bool Equals(ReadOnlyMemory<int> left, ReadOnlyMemory<int> right) => left.Span.SequenceEqual(right.Span);
        protected override ReadOnlyMemory<int>[] TestValues => [null, new ReadOnlyMemory<int>([], 0, 0), CreateValue(), CreateValue(), CreateValue()];
    }

    public class ArraySegmentCodecTests(ITestOutputHelper output) : FieldCodecTester<ArraySegment<int>, ArraySegmentCodec<int>>(output)
    {
        protected override ArraySegment<int> CreateValue()
        {
            var array = Enumerable.Range(0, Random.Next(120) + 50).Select(_ => Guid.NewGuid().GetHashCode()).ToArray();
            var start = Random.Next(array.Length);
            var len = Random.Next(array.Length - start);
            return new ArraySegment<int>(array, start, len);
        }

        protected override bool Equals(ArraySegment<int> left, ArraySegment<int> right) => left.SequenceEqual(right);
        protected override ArraySegment<int>[] TestValues => [null, new ArraySegment<int>([], 0, 0), CreateValue(), CreateValue(), CreateValue()];
    }

    public class ArraySegmentCopierTests(ITestOutputHelper output) : CopierTester<ArraySegment<int>, ArraySegmentCopier<int>>(output)
    {
        protected override ArraySegment<int> CreateValue()
        {
            var array = Enumerable.Range(0, Random.Next(120) + 50).Select(_ => Guid.NewGuid().GetHashCode()).ToArray();
            var start = Random.Next(array.Length);
            var len = Random.Next(array.Length - start);
            return new ArraySegment<int>(array, start, len);
        }

        protected override bool Equals(ArraySegment<int> left, ArraySegment<int> right) => left.SequenceEqual(right);
        protected override ArraySegment<int>[] TestValues => [null, new ArraySegment<int>([], 0, 0), CreateValue(), CreateValue(), CreateValue()];
    }

    public class ArrayCodecTests(ITestOutputHelper output) : FieldCodecTester<int[], ArrayCodec<int>>(output)
    {
        protected override int[] CreateValue() => Enumerable.Range(0, Random.Next(120) + 50).Select(_ => Guid.NewGuid().GetHashCode()).ToArray();
        protected override bool Equals(int[] left, int[] right) => ReferenceEquals(left, right) || left.SequenceEqual(right);
        protected override int[][] TestValues => [null, [], CreateValue(), CreateValue(), CreateValue()];
    }

    public class ArrayCopierTests(ITestOutputHelper output) : CopierTester<int[], ArrayCopier<int>>(output)
    {
        protected override int[] CreateValue() => Enumerable.Range(0, Random.Next(120) + 50).Select(_ => Guid.NewGuid().GetHashCode()).ToArray();
        protected override bool Equals(int[] left, int[] right) => ReferenceEquals(left, right) || left.SequenceEqual(right);
        protected override int[][] TestValues => [null, [], CreateValue(), CreateValue(), CreateValue()];
    }

    public class ImmutableArrayCodecTests(ITestOutputHelper output) : FieldCodecTester<ImmutableArray<int>, ImmutableArrayCodec<int>>(output)
    {
        protected override ImmutableArray<int> CreateValue() => Enumerable.Range(0, Random.Next(120) + 50).Select(_ => Guid.NewGuid().GetHashCode()).ToImmutableArray();
        protected override bool Equals(ImmutableArray<int> left, ImmutableArray<int> right) => (left.IsDefault && right.IsDefault) || left.SequenceEqual(right);
        protected override ImmutableArray<int>[] TestValues => [default, [], CreateValue(), CreateValue(), CreateValue()];
    }

    public class ImmutableArrayCopierTests(ITestOutputHelper output) : CopierTester<ImmutableArray<int>, ImmutableArrayCopier<int>>(output)
    {
        protected override ImmutableArray<int> CreateValue() => Enumerable.Range(0, Random.Next(120) + 50).Select(_ => Guid.NewGuid().GetHashCode()).ToImmutableArray();
        protected override bool Equals(ImmutableArray<int> left, ImmutableArray<int> right) => (left.IsDefault && right.IsDefault) || left.SequenceEqual(right);
        protected override ImmutableArray<int>[] TestValues => [default, [], CreateValue(), CreateValue(), CreateValue()];
    }

#if NET7_0_OR_GREATER
    public class UInt128CodecTests(ITestOutputHelper output) : FieldCodecTester<UInt128, UInt128Codec>(output)
    {
        protected override UInt128 CreateValue() => new (unchecked((ulong)Random.NextInt64()), unchecked((ulong)Random.NextInt64()));

        protected override UInt128[] TestValues =>
        [
            0,
            1,
            (UInt128)byte.MaxValue - 1,
            byte.MaxValue,
            (UInt128)byte.MaxValue + 1,
            (UInt128)ushort.MaxValue - 1,
            ushort.MaxValue,
            (UInt128)ushort.MaxValue + 1,
            (UInt128)uint.MaxValue - 1,
            uint.MaxValue,
            (UInt128)uint.MaxValue + 1,
            UInt128.MaxValue,
        ];

        protected override Action<Action<UInt128>> ValueProvider => assert => Gen.ULong.Select(Gen.ULong).Sample(value => assert(new (value.V0, value.V1)));
    }

    public class UInt128CopierTests(ITestOutputHelper output) : CopierTester<UInt128, IDeepCopier<UInt128>>(output)
    {
        protected override UInt128 CreateValue() => new (unchecked((ulong)Random.NextInt64()), unchecked((ulong)Random.NextInt64()));

        protected override UInt128[] TestValues =>
        [
            0,
            1,
            (UInt128)byte.MaxValue - 1,
            byte.MaxValue,
            (UInt128)byte.MaxValue + 1,
            (UInt128)ushort.MaxValue - 1,
            ushort.MaxValue,
            (UInt128)ushort.MaxValue + 1,
            (UInt128)uint.MaxValue - 1,
            uint.MaxValue,
            (UInt128)uint.MaxValue + 1,
            UInt128.MaxValue,
        ];

        protected override Action<Action<UInt128>> ValueProvider => assert => Gen.ULong.Select(Gen.ULong).Sample(value => assert(new (value.V0, value.V1)));
    }
#endif

    public class UInt64CodecTests(ITestOutputHelper output) : FieldCodecTester<ulong, UInt64Codec>(output)
    {
        protected override ulong CreateValue()
        {
            var msb = (ulong)Guid.NewGuid().GetHashCode() << 32;
            var lsb = (ulong)Guid.NewGuid().GetHashCode();
            return msb | lsb;
        }

        protected override ulong[] TestValues => new ulong[]
        {
            0,
            1,
            (ulong)byte.MaxValue - 1,
            byte.MaxValue,
            (ulong)byte.MaxValue + 1,
            (ulong)ushort.MaxValue - 1,
            ushort.MaxValue,
            (ulong)ushort.MaxValue + 1,
            (ulong)uint.MaxValue - 1,
            uint.MaxValue,
            (ulong)uint.MaxValue + 1,
            ulong.MaxValue,
        }
        .Concat(Enumerable.Range(1, 63).Select(i => 1ul << i))
        .ToArray();


        protected override Action<Action<ulong>> ValueProvider => Gen.ULong.ToValueProvider();
    }

    public class UInt64CopierTests(ITestOutputHelper output) : CopierTester<ulong, IDeepCopier<ulong>>(output)
    {
        protected override ulong CreateValue()
        {
            var msb = (ulong)Guid.NewGuid().GetHashCode() << 32;
            var lsb = (ulong)Guid.NewGuid().GetHashCode();
            return msb | lsb;
        }

        protected override ulong[] TestValues =>
        [
            0,
            1,
            (ulong)byte.MaxValue - 1,
            byte.MaxValue,
            (ulong)byte.MaxValue + 1,
            (ulong)ushort.MaxValue - 1,
            ushort.MaxValue,
            (ulong)ushort.MaxValue + 1,
            (ulong)uint.MaxValue - 1,
            uint.MaxValue,
            (ulong)uint.MaxValue + 1,
            ulong.MaxValue,
        ];

        protected override Action<Action<ulong>> ValueProvider => Gen.ULong.ToValueProvider();
    }

    public class UInt32CodecTests(ITestOutputHelper output) : FieldCodecTester<uint, UInt32Codec>(output)
    {
        protected override uint CreateValue() => (uint)Guid.NewGuid().GetHashCode();

        protected override uint[] TestValues => new uint[]
        {
            0,
            1,
            (uint)byte.MaxValue - 1,
            byte.MaxValue,
            (uint)byte.MaxValue + 1,
            (uint)ushort.MaxValue - 1,
            ushort.MaxValue,
            (uint)ushort.MaxValue + 1,
            uint.MaxValue,
        }
        .Concat(Enumerable.Range(1, 31).Select(i => 1u << i))
        .ToArray();

        protected override Action<Action<uint>> ValueProvider => Gen.UInt.ToValueProvider();
    }

    public class UInt32CopierTests(ITestOutputHelper output) : CopierTester<uint, IDeepCopier<uint>>(output)
    {
        protected override uint CreateValue() => (uint)Guid.NewGuid().GetHashCode();

        protected override uint[] TestValues =>
        [
            0,
            1,
            (uint)byte.MaxValue - 1,
            byte.MaxValue,
            (uint)byte.MaxValue + 1,
            (uint)ushort.MaxValue - 1,
            ushort.MaxValue,
            (uint)ushort.MaxValue + 1,
            uint.MaxValue,
        ];

        protected override Action<Action<uint>> ValueProvider => Gen.UInt.ToValueProvider();
    }

    public class UInt16CodecTests(ITestOutputHelper output) : FieldCodecTester<ushort, UInt16Codec>(output)
    {
        protected override ushort CreateValue() => (ushort)Guid.NewGuid().GetHashCode();
        protected override ushort[] TestValues =>
        [
            0,
            1,
            byte.MaxValue - 1,
            byte.MaxValue,
            byte.MaxValue + 1,
            ushort.MaxValue - 1,
            ushort.MaxValue,
        ];

        protected override Action<Action<ushort>> ValueProvider => Gen.UShort.ToValueProvider();
    }

    public class UInt16CopierTests(ITestOutputHelper output) : CopierTester<ushort, IDeepCopier<ushort>>(output)
    {
        protected override ushort CreateValue() => (ushort)Guid.NewGuid().GetHashCode();
        protected override ushort[] TestValues =>
        [
            0,
            1,
            byte.MaxValue - 1,
            byte.MaxValue,
            byte.MaxValue + 1,
            ushort.MaxValue - 1,
            ushort.MaxValue,
        ];

        protected override Action<Action<ushort>> ValueProvider => Gen.UShort.ToValueProvider();
    }

    public class ByteCodecTests(ITestOutputHelper output) : FieldCodecTester<byte, ByteCodec>(output)
    {
        protected override byte CreateValue() => (byte)Guid.NewGuid().GetHashCode();
        protected override byte[] TestValues => [0, 1, byte.MaxValue - 1, byte.MaxValue];

        protected override Action<Action<byte>> ValueProvider => Gen.Byte.ToValueProvider();
    }

    public class ByteCopierTests(ITestOutputHelper output) : CopierTester<byte, IDeepCopier<byte>>(output)
    {
        protected override byte CreateValue() => (byte)Guid.NewGuid().GetHashCode();
        protected override byte[] TestValues => [0, 1, byte.MaxValue - 1, byte.MaxValue];

        protected override Action<Action<byte>> ValueProvider => Gen.Byte.ToValueProvider();
    }

#if NET7_0_OR_GREATER
    public class Int128CodecTests(ITestOutputHelper output) : FieldCodecTester<Int128, Int128Codec>(output)
    {
        protected override Int128 CreateValue() => new (unchecked((ulong)Random.NextInt64()), unchecked((ulong)Random.NextInt64()));

        protected override Int128[] TestValues =>
        [
            0,
            1,
            (Int128)byte.MaxValue - 1,
            byte.MaxValue,
            (Int128)byte.MaxValue + 1,
            (Int128)ushort.MaxValue - 1,
            ushort.MaxValue,
            (Int128)ushort.MaxValue + 1,
            (Int128)uint.MaxValue - 1,
            uint.MaxValue,
            (Int128)uint.MaxValue + 1,
            Int128.MaxValue,
        ];

        protected override Action<Action<Int128>> ValueProvider => assert => Gen.ULong.Select(Gen.ULong).Sample(value => assert(new (value.V0, value.V1)));
    }

    public class Int128CopierTests(ITestOutputHelper output) : CopierTester<Int128, IDeepCopier<Int128>>(output)
    {
        protected override Int128 CreateValue() => new Int128(unchecked((ulong)Random.NextInt64()), unchecked((ulong)Random.NextInt64()));

        protected override Int128[] TestValues =>
        [
            0,
            1,
            (Int128)byte.MaxValue - 1,
            byte.MaxValue,
            (Int128)byte.MaxValue + 1,
            (Int128)ushort.MaxValue - 1,
            ushort.MaxValue,
            (Int128)ushort.MaxValue + 1,
            (Int128)uint.MaxValue - 1,
            uint.MaxValue,
            (Int128)uint.MaxValue + 1,
            Int128.MaxValue,
        ];

        protected override Action<Action<Int128>> ValueProvider => assert => Gen.ULong.Select(Gen.ULong).Sample(value => assert(new (value.V0, value.V1)));
    }
#endif

    public class Int64CodecTests(ITestOutputHelper output) : FieldCodecTester<long, Int64Codec>(output)
    {
        protected override long CreateValue()
        {
            var msb = (ulong)Guid.NewGuid().GetHashCode() << 32;
            var lsb = (ulong)Guid.NewGuid().GetHashCode();
            return (long)(msb | lsb);
        }

        protected override long[] TestValues =>
        [
            long.MinValue,
            -1,
            0,
            1,
            (long)sbyte.MaxValue - 1,
            sbyte.MaxValue,
            (long)sbyte.MaxValue + 1,
            (long)short.MaxValue - 1,
            short.MaxValue,
            (long)short.MaxValue + 1,
            (long)int.MaxValue - 1,
            int.MaxValue,
            (long)int.MaxValue + 1,
            long.MaxValue,
        ];

        protected override Action<Action<long>> ValueProvider => Gen.Long.ToValueProvider();
    }

    public class Int64CopierTests(ITestOutputHelper output) : CopierTester<long, IDeepCopier<long>>(output)
    {
        protected override long CreateValue()
        {
            var msb = (ulong)Guid.NewGuid().GetHashCode() << 32;
            var lsb = (ulong)Guid.NewGuid().GetHashCode();
            return (long)(msb | lsb);
        }

        protected override long[] TestValues =>
        [
            long.MinValue,
            -1,
            0,
            1,
            (long)sbyte.MaxValue - 1,
            sbyte.MaxValue,
            (long)sbyte.MaxValue + 1,
            (long)short.MaxValue - 1,
            short.MaxValue,
            (long)short.MaxValue + 1,
            (long)int.MaxValue - 1,
            int.MaxValue,
            (long)int.MaxValue + 1,
            long.MaxValue,
        ];

        protected override Action<Action<long>> ValueProvider => Gen.Long.ToValueProvider();
    }

    public class Int32CodecTests(ITestOutputHelper output) : FieldCodecTester<int, Int32Codec>(output)
    {
        protected override int CreateValue() => Guid.NewGuid().GetHashCode();

        protected override int[] TestValues =>
        [
            int.MinValue,
            -1,
            0,
            1,
            sbyte.MaxValue - 1,
            sbyte.MaxValue,
            sbyte.MaxValue + 1,
            short.MaxValue - 1,
            short.MaxValue,
            short.MaxValue + 1,
            int.MaxValue - 1,
            int.MaxValue,
        ];

        protected override Action<Action<int>> ValueProvider => Gen.Int.ToValueProvider();

        [Fact]
        public void CanRoundTripViaSerializer_WriteReadByteByByte()
        {
            var serializer = ServiceProvider.GetRequiredService<Serializer<int>>();

            foreach (var original in TestValues)
            {
                var buffer = new TestMultiSegmentBufferWriter(maxAllocationSize: 8);

                using var writerSession = SessionPool.GetSession();
                var writer = Writer.Create(buffer, writerSession);
                for (var i = 0; i < 5; i++)
                {
                    serializer.Serialize(original, ref writer);
                }

                writer.Commit();
                using var readerSession = SessionPool.GetSession();
                var reader = Reader.Create(buffer.GetReadOnlySequence(maxSegmentSize: 1), readerSession);
                for (var i = 0; i < 5; i++)
                {
                    var deserialized = serializer.Deserialize(ref reader);

                    Assert.True(Equals(original, deserialized), $"Deserialized value \"{deserialized}\" must equal original value \"{original}\"");
                }
            }
        }
    }

    public class Int32CopierTests(ITestOutputHelper output) : CopierTester<int, IDeepCopier<int>>(output)
    {
        protected override int CreateValue() => Guid.NewGuid().GetHashCode();

        protected override int[] TestValues =>
        [
            int.MinValue,
            -1,
            0,
            1,
            sbyte.MaxValue - 1,
            sbyte.MaxValue,
            sbyte.MaxValue + 1,
            short.MaxValue - 1,
            short.MaxValue,
            short.MaxValue + 1,
            int.MaxValue - 1,
            int.MaxValue,
        ];

        protected override Action<Action<int>> ValueProvider => Gen.Int.ToValueProvider();
    }

    public class Int16CodecTests(ITestOutputHelper output) : FieldCodecTester<short, Int16Codec>(output)
    {
        protected override short CreateValue() => (short)Guid.NewGuid().GetHashCode();

        protected override short[] TestValues =>
        [
            short.MinValue,
            -1,
            0,
            1,
            sbyte.MaxValue - 1,
            sbyte.MaxValue,
            sbyte.MaxValue + 1,
            short.MaxValue - 1,
            short.MaxValue
        ];

        protected override Action<Action<short>> ValueProvider => Gen.Short.ToValueProvider();
    }

    public class Int16CopierTests(ITestOutputHelper output) : CopierTester<short, IDeepCopier<short>>(output)
    {
        protected override short CreateValue() => (short)Guid.NewGuid().GetHashCode();

        protected override short[] TestValues =>
        [
            short.MinValue,
            -1,
            0,
            1,
            sbyte.MaxValue - 1,
            sbyte.MaxValue,
            sbyte.MaxValue + 1,
            short.MaxValue - 1,
            short.MaxValue
        ];

        protected override Action<Action<short>> ValueProvider => Gen.Short.ToValueProvider();
    }

    public class SByteCodecTests(ITestOutputHelper output) : FieldCodecTester<sbyte, SByteCodec>(output)
    {
        protected override sbyte CreateValue() => (sbyte)Guid.NewGuid().GetHashCode();

        protected override sbyte[] TestValues =>
        [
            sbyte.MinValue,
            -1,
            0,
            1,
            sbyte.MaxValue - 1,
            sbyte.MaxValue
        ];

        protected override Action<Action<sbyte>> ValueProvider => Gen.SByte.ToValueProvider();
    }

    public class SByteCopierTests(ITestOutputHelper output) : CopierTester<sbyte, IDeepCopier<sbyte>>(output)
    {
        protected override sbyte CreateValue() => (sbyte)Guid.NewGuid().GetHashCode();

        protected override sbyte[] TestValues =>
        [
            sbyte.MinValue,
            -1,
            0,
            1,
            sbyte.MaxValue - 1,
            sbyte.MaxValue
        ];

        protected override Action<Action<sbyte>> ValueProvider => Gen.SByte.ToValueProvider();
    }

    public class CharCodecTests(ITestOutputHelper output) : FieldCodecTester<char, CharCodec>(output)
    {
        private int _createValueCount;

        protected override char CreateValue() => (char)('!' + _createValueCount++ % ('~' - '!'));
        protected override char[] TestValues =>
        [
            (char)0,
            (char)1,
            (char)(byte.MaxValue - 1),
            (char)byte.MaxValue,
            (char)(byte.MaxValue + 1),
            (char)(ushort.MaxValue - 1),
            (char)ushort.MaxValue,
        ];

        protected override Action<Action<char>> ValueProvider => Gen.Char.ToValueProvider();
    }

    public class CharCopierTests(ITestOutputHelper output) : CopierTester<char, IDeepCopier<char>>(output)
    {
        private int _createValueCount;

        protected override char CreateValue() => (char)('!' + _createValueCount++ % ('~' - '!'));
        protected override char[] TestValues =>
        [
            (char)0,
            (char)1,
            (char)(byte.MaxValue - 1),
            (char)byte.MaxValue,
            (char)(byte.MaxValue + 1),
            (char)(ushort.MaxValue - 1),
            (char)ushort.MaxValue,
        ];

        protected override Action<Action<char>> ValueProvider => Gen.Char.ToValueProvider();
    }

    public class GuidCodecTests(ITestOutputHelper output) : FieldCodecTester<Guid, GuidCodec>(output)
    {
        protected override Guid CreateValue() => Guid.NewGuid();
        protected override Guid[] TestValues =>
        [
            Guid.Empty,
            Guid.Parse("4DEBD074-5DBB-45F6-ACB7-ED97D2AEE02F"),
            Guid.Parse("FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF")
        ];

        protected override Action<Action<Guid>> ValueProvider => Gen.Guid.ToValueProvider();
    }

    public class GuidCopierTests(ITestOutputHelper output) : CopierTester<Guid, IDeepCopier<Guid>>(output)
    {
        protected override Guid CreateValue() => Guid.NewGuid();
        protected override Guid[] TestValues =>
        [
            Guid.Empty,
            Guid.Parse("4DEBD074-5DBB-45F6-ACB7-ED97D2AEE02F"),
            Guid.Parse("FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF")
        ];

        protected override Action<Action<Guid>> ValueProvider => Gen.Guid.ToValueProvider();
    }

    public class TypeCodecTests(ITestOutputHelper output) : FieldCodecTester<Type, TypeSerializerCodec>(output)
    {
        private readonly Type[] _values =
        [
            null,
            typeof(Dictionary<Guid, List<string>>),
            typeof(Type).MakeByRefType(),
            typeof(Guid),
            typeof(int).MakePointerType(),
            typeof(string[]),
            typeof(string[,]),
            typeof(string[,]).MakePointerType(),
            typeof(string[,]).MakeByRefType(),
            typeof(Dictionary<,>),
            typeof(List<>),
            typeof(string)
        ];

        private int _valueIndex;

        protected override Type CreateValue() => _values[_valueIndex++ % _values.Length];
        protected override Type[] TestValues => _values;
    }

    public class TypeCopierTests(ITestOutputHelper output) : CopierTester<Type, IDeepCopier<Type>>(output)
    {
        private readonly Type[] _values =
        [
            null,
            typeof(Dictionary<Guid, List<string>>),
            typeof(Type).MakeByRefType(),
            typeof(Guid),
            typeof(int).MakePointerType(),
            typeof(string[]),
            typeof(string[,]),
            typeof(string[,]).MakePointerType(),
            typeof(string[,]).MakeByRefType(),
            typeof(Dictionary<,>),
            typeof(List<>),
            typeof(string)
        ];

        private int _valueIndex;

        protected override Type CreateValue() => _values[_valueIndex++ % _values.Length];
        protected override Type[] TestValues => _values;

        protected override bool IsImmutable => true;
    }

    public class FloatCodecTests(ITestOutputHelper output) : FieldCodecTester<float, FloatCodec>(output)
    {
        protected override float CreateValue() => float.MaxValue * (float)Random.NextDouble() * Math.Sign(Guid.NewGuid().GetHashCode());
        protected override float[] TestValues => [float.MinValue, 0, 1.0f, float.MaxValue];

        protected override Action<Action<float>> ValueProvider => Gen.Float.ToValueProvider();
    }

    public class FloatCopierTests(ITestOutputHelper output) : CopierTester<float, IDeepCopier<float>>(output)
    {
        protected override float CreateValue() => float.MaxValue * (float)Random.NextDouble() * Math.Sign(Guid.NewGuid().GetHashCode());
        protected override float[] TestValues => [float.MinValue, 0, 1.0f, float.MaxValue];

        protected override Action<Action<float>> ValueProvider => Gen.Float.ToValueProvider();
    }

#if NET5_0_OR_GREATER
    public class HalfCodecTests(ITestOutputHelper output) : FieldCodecTester<Half, HalfCodec>(output)
    {
        protected override Half CreateValue() => (Half)BitConverter.UInt16BitsToHalf((ushort)Random.Next(ushort.MinValue, ushort.MaxValue));
        protected override Half[] TestValues => [Half.MinValue, (Half)0, (Half)1.0f, Half.Tau, Half.E, Half.Epsilon, Half.MaxValue];

        protected override Action<Action<Half>> ValueProvider => assert => Gen.UShort.Sample(value => assert(BitConverter.UInt16BitsToHalf(value)));
    }

    public class HalfCopierTests(ITestOutputHelper output) : CopierTester<Half, IDeepCopier<Half>>(output)
    {
        protected override Half CreateValue() => (Half)BitConverter.UInt16BitsToHalf((ushort)Random.Next(ushort.MinValue, ushort.MaxValue));
        protected override Half[] TestValues => [Half.MinValue, (Half)0, (Half)1.0f, Half.Tau, Half.E, Half.Epsilon, Half.MaxValue];

        protected override Action<Action<Half>> ValueProvider => assert => Gen.UShort.Sample(value => assert(BitConverter.UInt16BitsToHalf(value)));
    }
#endif

    public class DoubleCodecTests(ITestOutputHelper output) : FieldCodecTester<double, DoubleCodec>(output)
    {
        protected override double CreateValue() => double.MaxValue * Random.NextDouble() * Math.Sign(Guid.NewGuid().GetHashCode());
        protected override double[] TestValues => [double.MinValue, 0, 1.0, double.MaxValue];

        protected override Action<Action<double>> ValueProvider => Gen.Double.ToValueProvider();
    }

    public class DoubleCopierTests(ITestOutputHelper output) : CopierTester<double, IDeepCopier<double>>(output)
    {
        protected override double CreateValue() => double.MaxValue * Random.NextDouble() * Math.Sign(Guid.NewGuid().GetHashCode());
        protected override double[] TestValues => [double.MinValue, 0, 1.0, double.MaxValue];

        protected override Action<Action<double>> ValueProvider => Gen.Double.ToValueProvider();
    }

    public class DecimalCodecTests(ITestOutputHelper output) : FieldCodecTester<decimal, DecimalCodec>(output)
    {
        protected override decimal CreateValue() => decimal.MaxValue * (decimal)Random.NextDouble() * Math.Sign(Guid.NewGuid().GetHashCode());
        protected override decimal[] TestValues => [decimal.MinValue, 0, 1.0M, decimal.MaxValue];
        protected override Action<Action<decimal>> ValueProvider => Gen.Decimal.ToValueProvider();
    }

    public class DecimalCopierTests(ITestOutputHelper output) : CopierTester<decimal, IDeepCopier<decimal>>(output)
    {
        protected override decimal CreateValue() => decimal.MaxValue * (decimal)Random.NextDouble() * Math.Sign(Guid.NewGuid().GetHashCode());
        protected override decimal[] TestValues => [decimal.MinValue, 0, 1.0M, decimal.MaxValue];
        protected override Action<Action<decimal>> ValueProvider => Gen.Decimal.ToValueProvider();
    }

    public class ListCodecTests(ITestOutputHelper output) : FieldCodecTester<List<int>, ListCodec<int>>(output)
    {
        protected override List<int> CreateValue()
        {
            var result = new List<int>();
            var len = Random.Next(17);
            for (var i = 0; i < len + 5; i++)
            {
                result.Add(Random.Next());
            }

            return result;
        }

        protected override bool Equals(List<int> left, List<int> right) => ReferenceEquals(left, right) || left.SequenceEqual(right);
        protected override List<int>[] TestValues => [null, new List<int>(), CreateValue(), CreateValue(), CreateValue()];
    }

    public class ListCopierTests(ITestOutputHelper output) : CopierTester<List<int>, ListCopier<int>>(output)
    {
        protected override List<int> CreateValue()
        {
            var result = new List<int>();
            var len = Random.Next(17);
            for (var i = 0; i < len + 5; i++)
            {
                result.Add(Random.Next());
            }

            return result;
        }

        protected override bool Equals(List<int> left, List<int> right) => ReferenceEquals(left, right) || left.SequenceEqual(right);
        protected override List<int>[] TestValues => [null, new List<int>(), CreateValue(), CreateValue(), CreateValue()];
    }

    public class ImmutableListCodecTests(ITestOutputHelper output) : FieldCodecTester<ImmutableList<int>, ImmutableListCodec<int>>(output)
    {
        protected override ImmutableList<int> CreateValue()
        {
            var result = ImmutableList.CreateBuilder<int>();
            var len = Random.Next(17);
            for (var i = 0; i < len + 5; i++)
            {
                result.Add(Random.Next());
            }

            return result.ToImmutable();
        }

        protected override bool Equals(ImmutableList<int> left, ImmutableList<int> right) => ReferenceEquals(left, right) || left.SequenceEqual(right);
        protected override ImmutableList<int>[] TestValues => [null, ImmutableList<int>.Empty, CreateValue(), CreateValue(), CreateValue()];
    }

    public class ImmutableListCopierTests(ITestOutputHelper output) : CopierTester<ImmutableList<int>, ImmutableListCopier<int>>(output)
    {
        protected override bool IsImmutable => true;
        protected override ImmutableList<int> CreateValue()
        {
            var result = ImmutableList.CreateBuilder<int>();
            var len = Random.Next(17);
            for (var i = 0; i < len + 5; i++)
            {
                result.Add(Random.Next());
            }

            return result.ToImmutable();
        }

        protected override bool Equals(ImmutableList<int> left, ImmutableList<int> right) => ReferenceEquals(left, right) || left.SequenceEqual(right);
        protected override ImmutableList<int>[] TestValues => [null, ImmutableList<int>.Empty, CreateValue(), CreateValue(), CreateValue()];
    }

    public class SortedListCodecTests(ITestOutputHelper output) : FieldCodecTester<SortedList<int, string>, SortedListCodec<int, string>>(output)
    {
        protected override SortedList<int, string> CreateValue()
        {
            var result = new SortedList<int, string>();
            var len = Random.Next(17);
            for (var i = 0; i < len + 5; i++)
            {
                var val = Random.Next();
                result.Add(val, val.ToString());
            }

            return result;
        }

        protected override bool Equals(SortedList<int, string> left, SortedList<int, string> right) => ReferenceEquals(left, right) || left.SequenceEqual(right);
        protected override SortedList<int, string>[] TestValues => [null, new SortedList<int, string>(), CreateValue(), CreateValue(), CreateValue()];
    }

    public class SortedListCopierTests(ITestOutputHelper output) : CopierTester<SortedList<int, string>, SortedListCopier<int, string>>(output)
    {
        protected override SortedList<int, string> CreateValue()
        {
            var result = new SortedList<int, string>();
            var len = Random.Next(17);
            for (var i = 0; i < len + 5; i++)
            {
                var val = Random.Next();
                result.Add(val, val.ToString());
            }

            return result;
        }

        protected override bool Equals(SortedList<int, string> left, SortedList<int, string> right) => ReferenceEquals(left, right) || left.SequenceEqual(right);
        protected override SortedList<int, string>[] TestValues => [null, new SortedList<int, string>(), CreateValue(), CreateValue(), CreateValue()];
    }

    public class SortedSetCodecTests(ITestOutputHelper output) : FieldCodecTester<SortedSet<int>, SortedSetCodec<int>>(output)
    {
        protected override SortedSet<int> CreateValue()
        {
            var result = new SortedSet<int>();
            var len = Random.Next(17);
            for (var i = 0; i < len + 5; i++)
            {
                var val = Random.Next();
                result.Add(val);
            }

            return result;
        }

        protected override bool Equals(SortedSet<int> left, SortedSet<int> right) => ReferenceEquals(left, right) || left.SequenceEqual(right);
        protected override SortedSet<int>[] TestValues => [null, [], CreateValue(), CreateValue(), CreateValue()];
    }

    public class SortedSetCopierTests(ITestOutputHelper output) : CopierTester<SortedSet<int>, SortedSetCopier<int>>(output)
    {
        protected override SortedSet<int> CreateValue()
        {
            var result = new SortedSet<int>();
            var len = Random.Next(17);
            for (var i = 0; i < len + 5; i++)
            {
                var val = Random.Next();
                result.Add(val);
            }

            return result;
        }

        protected override bool Equals(SortedSet<int> left, SortedSet<int> right) => ReferenceEquals(left, right) || left.SequenceEqual(right);
        protected override SortedSet<int>[] TestValues => [null, [], CreateValue(), CreateValue(), CreateValue()];
    }

    public class ImmutableSortedSetCodecTests(ITestOutputHelper output) : FieldCodecTester<ImmutableSortedSet<int>, ImmutableSortedSetCodec<int>>(output)
    {
        protected override ImmutableSortedSet<int> CreateValue()
        {
            var result = ImmutableSortedSet.CreateBuilder<int>();
            var len = Random.Next(17);
            for (var i = 0; i < len + 5; i++)
            {
                var val = Random.Next();
                result.Add(val);
            }

            return result.ToImmutable();
        }

        protected override bool Equals(ImmutableSortedSet<int> left, ImmutableSortedSet<int> right) => ReferenceEquals(left, right) || left.SequenceEqual(right);
        protected override ImmutableSortedSet<int>[] TestValues => [null, [], CreateValue(), CreateValue(), CreateValue()];
    }

    public class ImmutableSortedSetCopierTests(ITestOutputHelper output) : CopierTester<ImmutableSortedSet<int>, ImmutableSortedSetCopier<int>>(output)
    {
        protected override bool IsImmutable => true;
        protected override ImmutableSortedSet<int> CreateValue()
        {
            var result = ImmutableSortedSet.CreateBuilder<int>();
            var len = Random.Next(17);
            for (var i = 0; i < len + 5; i++)
            {
                var val = Random.Next();
                result.Add(val);
            }

            return result.ToImmutable();
        }

        protected override bool Equals(ImmutableSortedSet<int> left, ImmutableSortedSet<int> right) => ReferenceEquals(left, right) || left.SequenceEqual(right);
        protected override ImmutableSortedSet<int>[] TestValues => [null, [], CreateValue(), CreateValue(), CreateValue()];
    }

    public class ArrayListCodecTests(ITestOutputHelper output) : FieldCodecTester<ArrayList, ArrayListCodec>(output)
    {
        protected override ArrayList CreateValue()
        {
            var result = new ArrayList();
            var len = Random.Next(17);
            for (var i = 0; i < len + 5; i++)
            {
                result.Add(Random.Next());
            }

            return result;
        }

        protected override bool Equals(ArrayList left, ArrayList right) => ReferenceEquals(left, right) || left.ToArray().SequenceEqual(right.ToArray());
        protected override ArrayList[] TestValues => [null, new ArrayList(), CreateValue(), CreateValue(), CreateValue()];
    }

    public class ArrayListCopierTests(ITestOutputHelper output) : CopierTester<ArrayList, ArrayListCopier>(output)
    {
        protected override ArrayList CreateValue()
        {
            var result = new ArrayList();
            var len = Random.Next(17);
            for (var i = 0; i < len + 5; i++)
            {
                result.Add(Random.Next());
            }

            return result;
        }

        protected override bool Equals(ArrayList left, ArrayList right) => ReferenceEquals(left, right) || left.ToArray().SequenceEqual(right.ToArray());
        protected override ArrayList[] TestValues => [null, new ArrayList(), CreateValue(), CreateValue(), CreateValue()];
    }

    public class CollectionCodecTests(ITestOutputHelper output) : FieldCodecTester<Collection<int>, CollectionCodec<int>>(output)
    {
        protected override Collection<int> CreateValue()
        {
            var result = new Collection<int>();
            var len = Random.Next(17);
            for (var i = 0; i < len + 5; i++)
            {
                result.Add(Random.Next());
            }

            return result;
        }

        protected override bool Equals(Collection<int> left, Collection<int> right) => ReferenceEquals(left, right) || left.SequenceEqual(right);
        protected override Collection<int>[] TestValues => [null, new Collection<int>(), CreateValue(), CreateValue(), CreateValue()];
    }

    public class CollectionCopierTests(ITestOutputHelper output) : CopierTester<Collection<int>, CollectionCopier<int>>(output)
    {
        protected override Collection<int> CreateValue()
        {
            var result = new Collection<int>();
            var len = Random.Next(17);
            for (var i = 0; i < len + 5; i++)
            {
                result.Add(Random.Next());
            }

            return result;
        }

        protected override bool Equals(Collection<int> left, Collection<int> right) => ReferenceEquals(left, right) || left.SequenceEqual(right);
        protected override Collection<int>[] TestValues => [null, new Collection<int>(), CreateValue(), CreateValue(), CreateValue()];
    }

    public class ReadOnlyCollectionCodecTests(ITestOutputHelper output) : FieldCodecTester<ReadOnlyCollection<int>, ReadOnlyCollectionCodec<int>>(output)
    {
        protected override ReadOnlyCollection<int> CreateValue()
        {
            var result = new Collection<int>();
            var len = Random.Next(17);
            for (var i = 0; i < len + 5; i++)
            {
                result.Add(Random.Next());
            }

            return new ReadOnlyCollection<int>(result);
        }

        protected override bool Equals(ReadOnlyCollection<int> left, ReadOnlyCollection<int> right) => ReferenceEquals(left, right) || left.SequenceEqual(right);
        protected override ReadOnlyCollection<int>[] TestValues => [null, new ReadOnlyCollection<int>([]), CreateValue(), CreateValue(), CreateValue()];
    }

    public class ReadOnlyCollectionCopierTests(ITestOutputHelper output) : CopierTester<ReadOnlyCollection<int>, ReadOnlyCollectionCopier<int>>(output)
    {
        protected override ReadOnlyCollection<int> CreateValue()
        {
            var result = new Collection<int>();
            var len = Random.Next(17);
            for (var i = 0; i < len + 5; i++)
            {
                result.Add(Random.Next());
            }

            return new ReadOnlyCollection<int>(result);
        }

        protected override bool Equals(ReadOnlyCollection<int> left, ReadOnlyCollection<int> right) => ReferenceEquals(left, right) || left.SequenceEqual(right);
        protected override ReadOnlyCollection<int>[] TestValues => [null, new ReadOnlyCollection<int>([]), CreateValue(), CreateValue(), CreateValue()];
    }

    public class StackCodecTests(ITestOutputHelper output) : FieldCodecTester<Stack<int>, StackCodec<int>>(output)
    {
        protected override Stack<int> CreateValue()
        {
            var result = new Stack<int>();
            var len = Random.Next(17);
            for (var i = 0; i < len + 5; i++)
            {
                result.Push(Random.Next());
            }

            return result;
        }

        protected override bool Equals(Stack<int> left, Stack<int> right) => ReferenceEquals(left, right) || left.SequenceEqual(right);
        protected override Stack<int>[] TestValues => [null, new Stack<int>(), CreateValue(), CreateValue(), CreateValue()];
    }

    public class StackCopierTests(ITestOutputHelper output) : CopierTester<Stack<int>, StackCopier<int>>(output)
    {
        protected override Stack<int> CreateValue()
        {
            var result = new Stack<int>();
            var len = Random.Next(17);
            for (var i = 0; i < len + 5; i++)
            {
                result.Push(Random.Next());
            }

            return result;
        }

        protected override bool Equals(Stack<int> left, Stack<int> right) => ReferenceEquals(left, right) || left.SequenceEqual(right);
        protected override Stack<int>[] TestValues => [null, new Stack<int>(), CreateValue(), CreateValue(), CreateValue()];
    }

    public class ImmutableStackCodecTests(ITestOutputHelper output) : FieldCodecTester<ImmutableStack<int>, ImmutableStackCodec<int>>(output)
    {
        protected override ImmutableStack<int> CreateValue()
        {
            var result = new List<int>();
            var len = Random.Next(17);
            for (var i = 0; i < len + 5; i++)
            {
                result.Add(Random.Next());
            }

            return ImmutableStack.CreateRange(result);
        }

        protected override bool Equals(ImmutableStack<int> left, ImmutableStack<int> right) => ReferenceEquals(left, right) || left.SequenceEqual(right);
        protected override ImmutableStack<int>[] TestValues => [null, ImmutableStack<int>.Empty, CreateValue(), CreateValue(), CreateValue()];
    }

    public class ImmutableStackCopierTests(ITestOutputHelper output) : CopierTester<ImmutableStack<int>, ImmutableStackCopier<int>>(output)
    {
        protected override bool IsImmutable => true;
        protected override ImmutableStack<int> CreateValue()
        {
            var result = new List<int>();
            var len = Random.Next(17);
            for (var i = 0; i < len + 5; i++)
            {
                result.Add(Random.Next());
            }

            return ImmutableStack.CreateRange(result);
        }

        protected override bool Equals(ImmutableStack<int> left, ImmutableStack<int> right) => ReferenceEquals(left, right) || left.SequenceEqual(right);
        protected override ImmutableStack<int>[] TestValues => [null, ImmutableStack<int>.Empty, CreateValue(), CreateValue(), CreateValue()];
    }

    public class QueueCodecTests(ITestOutputHelper output) : FieldCodecTester<Queue<int>, QueueCodec<int>>(output)
    {
        protected override Queue<int> CreateValue()
        {
            var result = new Queue<int>();
            var len = Random.Next(17);
            for (var i = 0; i < len + 5; i++)
            {
                result.Enqueue(Random.Next());
            }

            return result;
        }

        protected override bool Equals(Queue<int> left, Queue<int> right) => ReferenceEquals(left, right) || left.SequenceEqual(right);
        protected override Queue<int>[] TestValues => [null, new Queue<int>(), CreateValue(), CreateValue(), CreateValue()];
    }

    public class QueueCopierTests(ITestOutputHelper output) : CopierTester<Queue<int>, QueueCopier<int>>(output)
    {
        protected override Queue<int> CreateValue()
        {
            var result = new Queue<int>();
            var len = Random.Next(17);
            for (var i = 0; i < len + 5; i++)
            {
                result.Enqueue(Random.Next());
            }

            return result;
        }

        protected override bool Equals(Queue<int> left, Queue<int> right) => ReferenceEquals(left, right) || left.SequenceEqual(right);
        protected override Queue<int>[] TestValues => [null, new Queue<int>(), CreateValue(), CreateValue(), CreateValue()];
    }

    public class ConcurrentQueueCodecTests(ITestOutputHelper output) : FieldCodecTester<ConcurrentQueue<int>, ConcurrentQueueCodec<int>>(output)
    {
        protected override ConcurrentQueue<int> CreateValue()
        {
            var result = new ConcurrentQueue<int>();
            var len = Random.Next(17);
            for (var i = 0; i < len + 5; i++)
            {
                result.Enqueue(Random.Next());
            }

            return result;
        }

        protected override bool Equals(ConcurrentQueue<int> left, ConcurrentQueue<int> right) => ReferenceEquals(left, right) || left.SequenceEqual(right);
        protected override ConcurrentQueue<int>[] TestValues => [null, new ConcurrentQueue<int>(), CreateValue(), CreateValue(), CreateValue()];
    }

    public class ConcurrentQueueCopierTests(ITestOutputHelper output) : CopierTester<ConcurrentQueue<int>, ConcurrentQueueCopier<int>>(output)
    {
        protected override ConcurrentQueue<int> CreateValue()
        {
            var result = new ConcurrentQueue<int>();
            var len = Random.Next(17);
            for (var i = 0; i < len + 5; i++)
            {
                result.Enqueue(Random.Next());
            }

            return result;
        }

        protected override bool Equals(ConcurrentQueue<int> left, ConcurrentQueue<int> right) => ReferenceEquals(left, right) || left.SequenceEqual(right);
        protected override ConcurrentQueue<int>[] TestValues => [null, new ConcurrentQueue<int>(), CreateValue(), CreateValue(), CreateValue()];
    }

    public class ImmutableQueueCodecTests(ITestOutputHelper output) : FieldCodecTester<ImmutableQueue<int>, ImmutableQueueCodec<int>>(output)
    {
        protected override ImmutableQueue<int> CreateValue()
        {
            var result = new List<int>();
            var len = Random.Next(17);
            for (var i = 0; i < len + 5; i++)
            {
                result.Add(Random.Next());
            }

            return ImmutableQueue.CreateRange<int>(result);
        }

        protected override bool Equals(ImmutableQueue<int> left, ImmutableQueue<int> right) => ReferenceEquals(left, right) || left.SequenceEqual(right);
        protected override ImmutableQueue<int>[] TestValues => [null, ImmutableQueue<int>.Empty, CreateValue(), CreateValue(), CreateValue()];
    }

    public class ImmutableQueueCopierTests(ITestOutputHelper output) : CopierTester<ImmutableQueue<int>, ImmutableQueueCopier<int>>(output)
    {
        protected override bool IsImmutable => true;
        protected override ImmutableQueue<int> CreateValue()
        {
            var result = new List<int>();
            var len = Random.Next(17);
            for (var i = 0; i < len + 5; i++)
            {
                result.Add(Random.Next());
            }

            return ImmutableQueue.CreateRange<int>(result);
        }

        protected override bool Equals(ImmutableQueue<int> left, ImmutableQueue<int> right) => ReferenceEquals(left, right) || left.SequenceEqual(right);
        protected override ImmutableQueue<int>[] TestValues => [null, ImmutableQueue<int>.Empty, CreateValue(), CreateValue(), CreateValue()];
    }

    public class DictionaryCodecTests(ITestOutputHelper output) : FieldCodecTester<Dictionary<string, int>, DictionaryCodec<string, int>>(output)
    {
        protected override Dictionary<string, int> CreateValue()
        {
            var result = new Dictionary<string, int>();
            var len = Random.Next(17);
            for (var i = 0; i < len + 5; i++)
            {
                result[Random.Next().ToString()] = Random.Next();
            }

            return result;
        }

        protected override Dictionary<string, int>[] TestValues => [null, new Dictionary<string, int>(), CreateValue(), CreateValue(), CreateValue()];
        protected override bool Equals(Dictionary<string, int> left, Dictionary<string, int> right) => ReferenceEquals(left, right) || left.SequenceEqual(right);
    }

    public class DictionaryCopierTests(ITestOutputHelper output) : CopierTester<Dictionary<string, int>, DictionaryCopier<string, int>>(output)
    {
        protected override Dictionary<string, int> CreateValue()
        {
            var result = new Dictionary<string, int>();
            var len = Random.Next(17);
            for (var i = 0; i < len + 5; i++)
            {
                result[Random.Next().ToString()] = Random.Next();
            }

            return result;
        }

        protected override Dictionary<string, int>[] TestValues => [null, new Dictionary<string, int>(), CreateValue(), CreateValue(), CreateValue()];
        protected override bool Equals(Dictionary<string, int> left, Dictionary<string, int> right) => ReferenceEquals(left, right) || left.SequenceEqual(right);
    }

    public class DictionaryWithComparerCodecTests(ITestOutputHelper output) : FieldCodecTester<Dictionary<string, int>, DictionaryCodec<string, int>>(output)
    {
        protected override int[] MaxSegmentSizes => [1024];
        private int _nextComparer;
        private readonly IEqualityComparer<string>[] _comparers =
        [
            new CaseInsensitiveEqualityComparer(),
#if NETCOREAPP3_1_OR_GREATER
            StringComparer.Ordinal,
            StringComparer.OrdinalIgnoreCase,
            EqualityComparer<string>.Default,
#endif
#if NET6_0_OR_GREATER
            StringComparer.InvariantCulture,
            StringComparer.InvariantCultureIgnoreCase,
            StringComparer.CurrentCulture,
            StringComparer.CurrentCultureIgnoreCase,
#endif
        ];

        protected override Dictionary<string, int> CreateValue()
        {
            var eqComparer = _comparers[_nextComparer++ % _comparers.Length];
            var result = new Dictionary<string, int>(eqComparer);
            var len = Random.Next(17);
            for (var i = 0; i < len + 5; i++)
            {
                var key = Guid.NewGuid().ToString();
                result[key.ToLowerInvariant()] = Random.Next();
                result[key.ToUpperInvariant()] = Random.Next();
            }

            return result;
        }

        protected override Dictionary<string, int>[] TestValues => [
            null,
            new Dictionary<string, int>(),
            CreateValue(),
            CreateValue(),
            CreateValue(),
            CreateValue(),
            CreateValue(),
            CreateValue(),
            CreateValue(),
            CreateValue()
        ];

        protected override bool Equals(Dictionary<string, int> left, Dictionary<string, int> right) => ReferenceEquals(left, right) || left.SequenceEqual(right);

        [GenerateSerializer]
        public class CaseInsensitiveEqualityComparer : IEqualityComparer<string>
        {
            public bool Equals(string left, string right)
            {
                if (left == null && right == null)
                {
                    return true;
                }
                else if (left == null || right == null)
                {
                    return false;
                }
                else if (left.ToUpper() == right.ToUpper())
                {
                    return true;
                }
                else
                {
                    return false;
                }
            }

            public int GetHashCode(string s) => s.ToUpper().GetHashCode();
        }
    }

    public class DictionaryWithComparerCopierTests(ITestOutputHelper output) : CopierTester<Dictionary<string, int>, DictionaryCopier<string, int>>(output)
    {
        protected override Dictionary<string, int> CreateValue()
        {
            var eqComparer = new DictionaryWithComparerCodecTests.CaseInsensitiveEqualityComparer();
            var result = new Dictionary<string, int>(eqComparer);
            var len = Random.Next(17);
            for (var i = 0; i < len + 5; i++)
            {
                result[Random.Next().ToString()] = Random.Next();
            }

            return result;
        }

        protected override Dictionary<string, int>[] TestValues => [null, new Dictionary<string, int>(), CreateValue(), CreateValue(), CreateValue()];

        protected override bool Equals(Dictionary<string, int> left, Dictionary<string, int> right) => ReferenceEquals(left, right) || left.SequenceEqual(right);
    }

    public class ConcurrentDictionaryCodecTests(ITestOutputHelper output) : FieldCodecTester<ConcurrentDictionary<string, int>, ConcurrentDictionaryCodec<string, int>>(output)
    {
        protected override ConcurrentDictionary<string, int> CreateValue()
        {
            var result = new ConcurrentDictionary<string, int>();
            var len = Random.Next(17);
            for (var i = 0; i < len + 5; i++)
            {
                result[Random.Next().ToString()] = Random.Next();
            }

            return result;
        }

        protected override ConcurrentDictionary<string, int>[] TestValues => [null, new ConcurrentDictionary<string, int>(), CreateValue(), CreateValue(), CreateValue()];

        protected override bool Equals(ConcurrentDictionary<string, int> left, ConcurrentDictionary<string, int> right)
        {
            // Order of the key-value pairs in the return value may not match the order of the key-value pairs in the surrogate
            if (ReferenceEquals(left, right))
            {
                return true;
            }
            else if (left.Keys.Count != right.Keys.Count)
            {
                return false;
            }

            foreach (string k in left.Keys)
            {
                if (!(right.ContainsKey(k) && left[k] == right[k]))
                {
                    return false;
                }
            }

            return true;
        }
    }

    public class ConcurrentDictionaryCopierTests(ITestOutputHelper output) : CopierTester<ConcurrentDictionary<string, int>, ConcurrentDictionaryCopier<string, int>>(output)
    {
        protected override ConcurrentDictionary<string, int> CreateValue()
        {
            var result = new ConcurrentDictionary<string, int>();
            var len = Random.Next(17);
            for (var i = 0; i < len + 5; i++)
            {
                result[Random.Next().ToString()] = Random.Next();
            }

            return result;
        }

        protected override ConcurrentDictionary<string, int>[] TestValues => [null, new ConcurrentDictionary<string, int>(), CreateValue(), CreateValue(), CreateValue()];

        protected override bool Equals(ConcurrentDictionary<string, int> left, ConcurrentDictionary<string, int> right)
        {
            // Order of the key-value pairs in the return value may not match the order of the key-value pairs in the surrogate
            if (ReferenceEquals(left, right))
            {
                return true;
            }
            else if (left.Keys.Count != right.Keys.Count)
            {
                return false;
            }

            foreach (string k in left.Keys)
            {
                if (!(right.ContainsKey(k) && left[k] == right[k]))
                {
                    return false;
                }
            }

            return true;
        }
    }

    public class ReadOnlyDictionaryCodecTests(ITestOutputHelper output) : FieldCodecTester<ReadOnlyDictionary<string, int>, ReadOnlyDictionaryCodec<string, int>>(output)
    {
        protected override ReadOnlyDictionary<string, int> CreateValue()
        {
            var dict = new Dictionary<string, int>();
            var len = Random.Next(17);
            for (var i = 0; i < len + 5; i++)
            {
                dict[Random.Next().ToString()] = Random.Next();
            }

            return new ReadOnlyDictionary<string, int>(dict);
        }

        protected override ReadOnlyDictionary<string, int>[] TestValues => [null, new ReadOnlyDictionary<string, int>(new Dictionary<string, int>()), CreateValue(), CreateValue(), CreateValue()];
        protected override bool Equals(ReadOnlyDictionary<string, int> left, ReadOnlyDictionary<string, int> right) => ReferenceEquals(left, right) || left.SequenceEqual(right);
    }

    public class ReadOnlyDictionaryCopierTests(ITestOutputHelper output) : CopierTester<ReadOnlyDictionary<string, int>, ReadOnlyDictionaryCopier<string, int>>(output)
    {
        protected override ReadOnlyDictionary<string, int> CreateValue()
        {
            var dict = new Dictionary<string, int>();
            var len = Random.Next(17);
            for (var i = 0; i < len + 5; i++)
            {
                dict[Random.Next().ToString()] = Random.Next();
            }

            return new ReadOnlyDictionary<string, int>(dict);
        }

        protected override ReadOnlyDictionary<string, int>[] TestValues => [null, new ReadOnlyDictionary<string, int>(new Dictionary<string, int>()), CreateValue(), CreateValue(), CreateValue()];
        protected override bool Equals(ReadOnlyDictionary<string, int> left, ReadOnlyDictionary<string, int> right) => ReferenceEquals(left, right) || left.SequenceEqual(right);
    }

    public class ImmutableDictionaryCodecTests(ITestOutputHelper output) : FieldCodecTester<ImmutableDictionary<string, int>, ImmutableDictionaryCodec<string, int>>(output)
    {
        protected override ImmutableDictionary<string, int> CreateValue()
        {
            var result = ImmutableDictionary.CreateBuilder<string, int>();
            var len = Random.Next(17);
            for (var i = 0; i < len + 5; i++)
            {
                result[Random.Next().ToString()] = Random.Next();
            }

            return result.ToImmutable();
        }

        protected override ImmutableDictionary<string, int>[] TestValues => [null, ImmutableDictionary<string, int>.Empty, CreateValue(), CreateValue(), CreateValue()];
        protected override bool Equals(ImmutableDictionary<string, int> left, ImmutableDictionary<string, int> right) => ReferenceEquals(left, right) || left.SequenceEqual(right);
    }

    public class ImmutableDictionaryCopierTests(ITestOutputHelper output) : CopierTester<ImmutableDictionary<string, int>, ImmutableDictionaryCopier<string, int>>(output)
    {
        protected override bool IsImmutable => true;
        protected override ImmutableDictionary<string, int> CreateValue()
        {
            var result = ImmutableDictionary.CreateBuilder<string, int>();
            var len = Random.Next(17);
            for (var i = 0; i < len + 5; i++)
            {
                result[Random.Next().ToString()] = Random.Next();
            }

            return result.ToImmutable();
        }

        protected override ImmutableDictionary<string, int>[] TestValues => [null, ImmutableDictionary<string, int>.Empty, CreateValue(), CreateValue(), CreateValue()];
        protected override bool Equals(ImmutableDictionary<string, int> left, ImmutableDictionary<string, int> right) => ReferenceEquals(left, right) || left.SequenceEqual(right);
    }

    public class SortedDictionaryCodecTests(ITestOutputHelper output) : FieldCodecTester<SortedDictionary<string, int>, SortedDictionaryCodec<string, int>>(output)
    {
        protected override SortedDictionary<string, int> CreateValue()
        {
            var result = new SortedDictionary<string, int>();
            var len = Random.Next(17);
            for (var i = 0; i < len + 5; i++)
            {
                result[Random.Next().ToString()] = Random.Next();
            }

            return result;
        }

        protected override SortedDictionary<string, int>[] TestValues => [null, new SortedDictionary<string, int>(), CreateValue(), CreateValue(), CreateValue()];
        protected override bool Equals(SortedDictionary<string, int> left, SortedDictionary<string, int> right) => ReferenceEquals(left, right) || left.SequenceEqual(right);
    }

    public class SortedDictionaryCopierTests(ITestOutputHelper output) : CopierTester<SortedDictionary<string, int>, SortedDictionaryCopier<string, int>>(output)
    {
        protected override SortedDictionary<string, int> CreateValue()
        {
            var result = new SortedDictionary<string, int>();
            var len = Random.Next(17);
            for (var i = 0; i < len + 5; i++)
            {
                result[Random.Next().ToString()] = Random.Next();
            }

            return result;
        }

        protected override SortedDictionary<string, int>[] TestValues => [null, new SortedDictionary<string, int>(), CreateValue(), CreateValue(), CreateValue()];
        protected override bool Equals(SortedDictionary<string, int> left, SortedDictionary<string, int> right) => ReferenceEquals(left, right) || left.SequenceEqual(right);
    }

    public class ImmutableSortedDictionaryCodecTests(ITestOutputHelper output) : FieldCodecTester<ImmutableSortedDictionary<string, int>, ImmutableSortedDictionaryCodec<string, int>>(output)
    {
        protected override ImmutableSortedDictionary<string, int> CreateValue()
        {
            var result = ImmutableSortedDictionary.CreateBuilder<string, int>();
            var len = Random.Next(17);
            for (var i = 0; i < len + 5; i++)
            {
                result[Random.Next().ToString()] = Random.Next();
            }

            return result.ToImmutable();
        }

        protected override ImmutableSortedDictionary<string, int>[] TestValues => [null, ImmutableSortedDictionary<string, int>.Empty, CreateValue(), CreateValue(), CreateValue()];
        protected override bool Equals(ImmutableSortedDictionary<string, int> left, ImmutableSortedDictionary<string, int> right) => ReferenceEquals(left, right) || left.SequenceEqual(right);
    }

    public class ImmutableSortedDictionaryCopierTests(ITestOutputHelper output) : CopierTester<ImmutableSortedDictionary<string, int>, ImmutableSortedDictionaryCopier<string, int>>(output)
    {
        protected override bool IsImmutable => true;

        protected override ImmutableSortedDictionary<string, int> CreateValue()
        {
            var result = ImmutableSortedDictionary.CreateBuilder<string, int>();
            var len = Random.Next(17);
            for (var i = 0; i < len + 5; i++)
            {
                result[Random.Next().ToString()] = Random.Next();
            }

            return result.ToImmutable();
        }

        protected override ImmutableSortedDictionary<string, int>[] TestValues => [null, ImmutableSortedDictionary<string, int>.Empty, CreateValue(), CreateValue(), CreateValue()];
        protected override bool Equals(ImmutableSortedDictionary<string, int> left, ImmutableSortedDictionary<string, int> right) => ReferenceEquals(left, right) || left.SequenceEqual(right);
    }

    public class NameValueCollectionCodecTests(ITestOutputHelper output) : FieldCodecTester<NameValueCollection, NameValueCollectionCodec>(output)
    {
        protected override NameValueCollection CreateValue()
        {
            var result = new NameValueCollection();
            var len = Random.Next(17);
            for (var i = 0; i < len + 5; i++)
            {
                result[Random.Next().ToString()] = Random.Next().ToString();
            }

            return result;
        }

        protected override NameValueCollection[] TestValues => [null, new NameValueCollection(), CreateValue(), CreateValue(), CreateValue()];
        protected override bool Equals(NameValueCollection left, NameValueCollection right) => ReferenceEquals(left, right)
            || (left.AllKeys.OrderBy(key => key).SequenceEqual(right.AllKeys.OrderBy(key => key)) && left.AllKeys.All(key => string.Equals(left[key], right[key], StringComparison.Ordinal)));
    }

    public class NameValueCollectionCopierTests(ITestOutputHelper output) : CopierTester<NameValueCollection, NameValueCollectionCopier>(output)
    {
        protected override NameValueCollection CreateValue()
        {
            var result = new NameValueCollection();
            var len = Random.Next(17);
            for (var i = 0; i < len + 5; i++)
            {
                result[Random.Next().ToString()] = Random.Next().ToString();
            }

            return result;
        }

        protected override NameValueCollection[] TestValues => [null, new NameValueCollection(), CreateValue(), CreateValue(), CreateValue()];
        protected override bool Equals(NameValueCollection left, NameValueCollection right) => ReferenceEquals(left, right)
            || (left.AllKeys.OrderBy(key => key).SequenceEqual(right.AllKeys.OrderBy(key => key)) && left.AllKeys.All(key => string.Equals(left[key], right[key], StringComparison.Ordinal)));
    }

    public class IPAddressTests(ITestOutputHelper output) : FieldCodecTester<IPAddress, IPAddressCodec>(output)
    {
        protected override int[] MaxSegmentSizes => [32];

        protected override IPAddress[] TestValues => [null, IPAddress.Any, IPAddress.IPv6Any, IPAddress.IPv6Loopback, IPAddress.IPv6None, IPAddress.Loopback, IPAddress.Parse("123.123.10.3"), CreateValue()];

        protected override IPAddress CreateValue()
        {
            byte[] bytes;
            if (Random.Next(1) == 0)
            {
                bytes = new byte[4];
            }
            else
            {
                bytes = new byte[16];
            }
            Random.NextBytes(bytes);
            return new IPAddress(bytes);
        }
    }

    public class IPAddressCopierTests(ITestOutputHelper output) : CopierTester<IPAddress, IDeepCopier<IPAddress>>(output)
    {
        protected override IPAddress[] TestValues => [null, IPAddress.Any, IPAddress.IPv6Any, IPAddress.IPv6Loopback, IPAddress.IPv6None, IPAddress.Loopback, IPAddress.Parse("123.123.10.3"), CreateValue()];

        protected override IPAddress CreateValue()
        {
            byte[] bytes;
            if (Random.Next(1) == 0)
            {
                bytes = new byte[4];
            }
            else
            {
                bytes = new byte[16];
            }
            Random.NextBytes(bytes);
            return new IPAddress(bytes);
        }

        protected override bool IsImmutable => true;
    }

    public class IPEndPointTests(ITestOutputHelper output) : FieldCodecTester<IPEndPoint, IPEndPointCodec>(output)
    {
        protected override int[] MaxSegmentSizes => [32];

        protected override IPEndPoint[] TestValues =>
           [null,
            new(IPAddress.Any, Random.Next(ushort.MaxValue)),
            new(IPAddress.IPv6Any, Random.Next(ushort.MaxValue)),
            new(IPAddress.IPv6Loopback, Random.Next(ushort.MaxValue)),
            new(IPAddress.IPv6None, Random.Next(ushort.MaxValue)),
            new(IPAddress.Loopback, Random.Next(ushort.MaxValue)),
            new(IPAddress.Parse("123.123.10.3"), Random.Next(ushort.MaxValue)),
            CreateValue()];

        protected override IPEndPoint CreateValue()
        {
            byte[] bytes;
            if (Random.Next(1) == 0)
            {
                bytes = new byte[4];
            }
            else
            {
                bytes = new byte[16];
            }
            Random.NextBytes(bytes);
            return new IPEndPoint(new IPAddress(bytes), Random.Next((int)ushort.MaxValue));
        }
    }

    public class IPEndPointCopierTests(ITestOutputHelper output) : CopierTester<IPEndPoint, IDeepCopier<IPEndPoint>>(output)
    {
        protected override IPEndPoint[] TestValues =>
           [null,
            new(IPAddress.Any, Random.Next(ushort.MaxValue)),
            new(IPAddress.IPv6Any, Random.Next(ushort.MaxValue)),
            new(IPAddress.IPv6Loopback, Random.Next(ushort.MaxValue)),
            new(IPAddress.IPv6None, Random.Next(ushort.MaxValue)),
            new(IPAddress.Loopback, Random.Next(ushort.MaxValue)),
            new(IPAddress.Parse("123.123.10.3"), Random.Next(ushort.MaxValue)),
            CreateValue()];

        protected override IPEndPoint CreateValue()
        {
            byte[] bytes;
            if (Random.Next(1) == 0)
            {
                bytes = new byte[4];
            }
            else
            {
                bytes = new byte[16];
            }
            Random.NextBytes(bytes);
            return new IPEndPoint(new IPAddress(bytes), Random.Next((int)ushort.MaxValue));
        }

        protected override bool IsImmutable => true;
    }

    public class HashSetTests(ITestOutputHelper output) : FieldCodecTester<HashSet<string>, HashSetCodec<string>>(output)
    {
        protected override HashSet<string> CreateValue()
        {
            var result = new HashSet<string>();
            var len = Random.Next(17);
            for (var i = 0; i < len + 5; i++)
            {
                _ = result.Add(Random.Next().ToString());
            }

            return result;
        }

        protected override HashSet<string>[] TestValues => [null, new HashSet<string>(), CreateValue(), CreateValue(), CreateValue()];

        protected override bool Equals(HashSet<string> left, HashSet<string> right) => ReferenceEquals(left, right) || left.SetEquals(right);
    }

    public class HashSetCopierTests(ITestOutputHelper output) : CopierTester<HashSet<string>, HashSetCopier<string>>(output)
    {
        protected override HashSet<string> CreateValue()
        {
            var result = new HashSet<string>();
            var len = Random.Next(17);
            for (var i = 0; i < len + 5; i++)
            {
                _ = result.Add(Random.Next().ToString());
            }

            return result;
        }

        protected override HashSet<string>[] TestValues => [null, new HashSet<string>(), CreateValue(), CreateValue(), CreateValue()];

        protected override bool Equals(HashSet<string> left, HashSet<string> right) => ReferenceEquals(left, right) || left.SetEquals(right);
    }

    public class ImmutableHashSetTests(ITestOutputHelper output) : FieldCodecTester<ImmutableHashSet<string>, ImmutableHashSetCodec<string>>(output)
    {
        protected override ImmutableHashSet<string> CreateValue()
        {
            var hashSet = new HashSet<string>();
            var len = Random.Next(17);
            for (var i = 0; i < len + 5; i++)
            {
                _ = hashSet.Add(Random.Next().ToString());
            }

            return hashSet.ToImmutableHashSet();
        }

        protected override ImmutableHashSet<string>[] TestValues => [null, [], CreateValue(), CreateValue(), CreateValue()];

        protected override bool Equals(ImmutableHashSet<string> left, ImmutableHashSet<string> right) => ReferenceEquals(left, right) || left.SetEquals(right);
    }

    public class ImmutableHashSetCopierTests(ITestOutputHelper output) : CopierTester<ImmutableHashSet<string>, ImmutableHashSetCopier<string>>(output)
    {
        protected override ImmutableHashSet<string> CreateValue()
        {
            var hashSet = new HashSet<string>();
            var len = Random.Next(17);
            for (var i = 0; i < len + 5; i++)
            {
                _ = hashSet.Add(Random.Next().ToString());
            }

            return hashSet.ToImmutableHashSet();
        }

        protected override ImmutableHashSet<string>[] TestValues => [null, [], CreateValue(), CreateValue(), CreateValue()];

        protected override bool Equals(ImmutableHashSet<string> left, ImmutableHashSet<string> right) => ReferenceEquals(left, right) || left.SetEquals(right);

        protected override bool IsImmutable => true;
    }

    public sealed class ImmutableHashSetMutableCopierTests(ITestOutputHelper output) : CopierTester<ImmutableHashSet<object>, ImmutableHashSetCopier<object>>(output)
    {
        protected override ImmutableHashSet<object> CreateValue()
        {
            var hashSet = new HashSet<object>();
            var len = Random.Next(17);
            for (var i = 0; i < len + 5; i++)
            {
                _ = hashSet.Add(Random.Next());
            }

            return hashSet.ToImmutableHashSet();
        }

        protected override ImmutableHashSet<object>[] TestValues => [null, [], CreateValue(), CreateValue(), CreateValue()];

        protected override bool Equals(ImmutableHashSet<object> left, ImmutableHashSet<object> right) => ReferenceEquals(left, right) || left.SetEquals(right);

        protected override bool IsImmutable => false;
    }

    public class UriTests(ITestOutputHelper output) : FieldCodecTester<Uri, UriCodec>(output)
    {
        protected override int[] MaxSegmentSizes => [128];
        protected override Uri CreateValue() => new Uri($"http://www.{Guid.NewGuid()}.com/");

        protected override Uri[] TestValues => [null, CreateValue(), CreateValue(), CreateValue(), CreateValue()];

        protected override bool Equals(Uri left, Uri right) => ReferenceEquals(left, right) || left == right;
    }

    public class UriCopierTests(ITestOutputHelper output) : CopierTester<Uri, IDeepCopier<Uri>>(output)
    {
        protected override Uri CreateValue() => new Uri($"http://www.{Guid.NewGuid()}.com/");

        protected override Uri[] TestValues => [null, CreateValue(), CreateValue(), CreateValue(), CreateValue()];

        protected override bool Equals(Uri left, Uri right) => ReferenceEquals(left, right) || left == right;

        protected override bool IsImmutable => true;
    }

    public class FSharpOptionTests(ITestOutputHelper output) : FieldCodecTester<FSharpOption<Guid>, FSharpOptionCodec<Guid>>(output)
    {
        protected override FSharpOption<Guid>[] TestValues => [null, FSharpOption<Guid>.None, FSharpOption<Guid>.Some(Guid.Empty), FSharpOption<Guid>.Some(Guid.NewGuid())];

        protected override FSharpOption<Guid> CreateValue() => FSharpOption<Guid>.Some(Guid.NewGuid());
    }

    public class FSharpOptionCopierTests(ITestOutputHelper output) : CopierTester<FSharpOption<Guid>, FSharpOptionCopier<Guid>>(output)
    {
        protected override FSharpOption<Guid>[] TestValues => [null, FSharpOption<Guid>.None, FSharpOption<Guid>.Some(Guid.Empty), FSharpOption<Guid>.Some(Guid.NewGuid())];

        protected override FSharpOption<Guid> CreateValue() => FSharpOption<Guid>.Some(Guid.NewGuid());

        protected override bool Equals(FSharpOption<Guid> left, FSharpOption<Guid> right) => ReferenceEquals(left, right) || left.GetType() == right.GetType() && left.Value.Equals(right.Value);
    }

    public class FSharpOptionTests2(ITestOutputHelper output) : FieldCodecTester<FSharpOption<object>, FSharpOptionCodec<object>>(output)
    {
        protected override FSharpOption<object>[] TestValues => [null, FSharpOption<object>.Some(null), FSharpOption<object>.None, FSharpOption<object>.Some(Guid.Empty), FSharpOption<object>.Some(Guid.NewGuid())];

        protected override FSharpOption<object> CreateValue() => FSharpOption<object>.Some(Guid.NewGuid());
    }

    public class FSharpRefTests(ITestOutputHelper output) : FieldCodecTester<FSharpRef<Guid>, FSharpRefCodec<Guid>>(output)
    {
        protected override FSharpRef<Guid>[] TestValues => [null, new FSharpRef<Guid>(default), new FSharpRef<Guid>(Guid.Empty), new FSharpRef<Guid>(Guid.NewGuid())];

        protected override FSharpRef<Guid> CreateValue() => new(Guid.NewGuid());

        protected override bool Equals(FSharpRef<Guid> left, FSharpRef<Guid> right) => ReferenceEquals(left, right) || EqualityComparer<Guid>.Default.Equals(left.Value, right.Value);
    }

    public class FSharpRefCopierTests(ITestOutputHelper output) : CopierTester<FSharpRef<Guid>, FSharpRefCopier<Guid>>(output)
    {
        protected override FSharpRef<Guid>[] TestValues => [null, new FSharpRef<Guid>(default), new FSharpRef<Guid>(Guid.Empty), new FSharpRef<Guid>(Guid.NewGuid())];

        protected override FSharpRef<Guid> CreateValue() => new(Guid.NewGuid());

        protected override bool Equals(FSharpRef<Guid> left, FSharpRef<Guid> right) => ReferenceEquals(left, right) || left.GetType() == right.GetType() && left.Value.Equals(right.Value);
    }

    public class FSharpValueOptionTests(ITestOutputHelper output) : FieldCodecTester<FSharpValueOption<Guid>, FSharpValueOptionCodec<Guid>>(output)
    {
        protected override FSharpValueOption<Guid>[] TestValues => [default, FSharpValueOption<Guid>.None, FSharpValueOption<Guid>.Some(Guid.Empty), FSharpValueOption<Guid>.Some(Guid.NewGuid())];

        protected override FSharpValueOption<Guid> CreateValue() => FSharpValueOption<Guid>.Some(Guid.NewGuid());
    }

    public class FSharpValueOptionCopierTests(ITestOutputHelper output) : CopierTester<FSharpValueOption<Guid>, FSharpValueOptionCopier<Guid>>(output)
    {
        protected override FSharpValueOption<Guid>[] TestValues => [default, FSharpValueOption<Guid>.None, FSharpValueOption<Guid>.Some(Guid.Empty), FSharpValueOption<Guid>.Some(Guid.NewGuid())];

        protected override FSharpValueOption<Guid> CreateValue() => FSharpValueOption<Guid>.Some(Guid.NewGuid());

        protected override bool Equals(FSharpValueOption<Guid> left, FSharpValueOption<Guid> right) => left.IsNone && right.IsNone || left.IsSome && right.IsSome && left.Value.Equals(right.Value);
    }

    public class FSharpResultTests(ITestOutputHelper output) : FieldCodecTester<FSharpResult<int, Guid>, FSharpResultCodec<int, Guid>>(output)
    {
        protected override FSharpResult<int, Guid>[] TestValues => [FSharpResult<int, Guid>.NewOk(-1), FSharpResult<int, Guid>.NewError(Guid.NewGuid())];

        protected override FSharpResult<int, Guid> CreateValue() => FSharpResult<int, Guid>.NewOk(0);
    }

    public class FSharpChoice2Tests(ITestOutputHelper output) : FieldCodecTester<FSharpChoice<int, Guid>, FSharpChoiceCodec<int, Guid>>(output)
    {
        protected override FSharpChoice<int, Guid>[] TestValues => [FSharpChoice<int, Guid>.NewChoice1Of2(-1), FSharpChoice<int, Guid>.NewChoice2Of2(Guid.NewGuid())];

        protected override FSharpChoice<int, Guid> CreateValue() => FSharpChoice<int, Guid>.NewChoice1Of2(0);
    }

    public class FSharpChoice2CopierTests(ITestOutputHelper output) : CopierTester<FSharpChoice<int, Guid>, FSharpChoiceCopier<int, Guid>>(output)
    {
        protected override FSharpChoice<int, Guid>[] TestValues => [FSharpChoice<int, Guid>.NewChoice1Of2(-1), FSharpChoice<int, Guid>.NewChoice2Of2(Guid.NewGuid())];

        protected override FSharpChoice<int, Guid> CreateValue() => FSharpChoice<int, Guid>.NewChoice1Of2(0);
    }

    public class FSharpChoice3Tests(ITestOutputHelper output) : FieldCodecTester<FSharpChoice<int, Guid, Guid>, FSharpChoiceCodec<int, Guid, Guid>>(output)
    {
        protected override FSharpChoice<int, Guid, Guid>[] TestValues => [FSharpChoice<int, Guid, Guid>.NewChoice1Of3(-1), FSharpChoice<int, Guid, Guid>.NewChoice3Of3(Guid.NewGuid())];

        protected override FSharpChoice<int, Guid, Guid> CreateValue() => FSharpChoice<int, Guid, Guid>.NewChoice1Of3(0);
    }

    public class FSharpChoice3CopierTests(ITestOutputHelper output) : CopierTester<FSharpChoice<int, Guid, Guid>, FSharpChoiceCopier<int, Guid, Guid>>(output)
    {
        protected override FSharpChoice<int, Guid, Guid>[] TestValues => [FSharpChoice<int, Guid, Guid>.NewChoice1Of3(-1), FSharpChoice<int, Guid, Guid>.NewChoice3Of3(Guid.NewGuid())];

        protected override FSharpChoice<int, Guid, Guid> CreateValue() => FSharpChoice<int, Guid, Guid>.NewChoice1Of3(0);
    }

    public class FSharpChoice4Tests(ITestOutputHelper output) : FieldCodecTester<FSharpChoice<int, Guid, Guid, Guid>, FSharpChoiceCodec<int, Guid, Guid, Guid>>(output)
    {
        protected override FSharpChoice<int, Guid, Guid, Guid>[] TestValues => [FSharpChoice<int, Guid, Guid, Guid>.NewChoice1Of4(-1), FSharpChoice<int, Guid, Guid, Guid>.NewChoice4Of4(Guid.NewGuid())];

        protected override FSharpChoice<int, Guid, Guid, Guid> CreateValue() => FSharpChoice<int, Guid, Guid, Guid>.NewChoice1Of4(0);
    }

    public class FSharpChoice4CopierTests(ITestOutputHelper output) : CopierTester<FSharpChoice<int, Guid, Guid, Guid>, FSharpChoiceCopier<int, Guid, Guid, Guid>>(output)
    {
        protected override FSharpChoice<int, Guid, Guid, Guid>[] TestValues => [FSharpChoice<int, Guid, Guid, Guid>.NewChoice1Of4(-1), FSharpChoice<int, Guid, Guid, Guid>.NewChoice4Of4(Guid.NewGuid())];

        protected override FSharpChoice<int, Guid, Guid, Guid> CreateValue() => FSharpChoice<int, Guid, Guid, Guid>.NewChoice1Of4(0);
    }

    public class FSharpChoice5Tests(ITestOutputHelper output) : FieldCodecTester<FSharpChoice<int, Guid, Guid, Guid, Guid>, FSharpChoiceCodec<int, Guid, Guid, Guid, Guid>>(output)
    {
        protected override FSharpChoice<int, Guid, Guid, Guid, Guid>[] TestValues => [FSharpChoice<int, Guid, Guid, Guid, Guid>.NewChoice1Of5(-1), FSharpChoice<int, Guid, Guid, Guid, Guid>.NewChoice5Of5(Guid.NewGuid())];

        protected override FSharpChoice<int, Guid, Guid, Guid, Guid> CreateValue() => FSharpChoice<int, Guid, Guid, Guid, Guid>.NewChoice1Of5(0);
    }

    public class FSharpChoice5CopierTests(ITestOutputHelper output) : CopierTester<FSharpChoice<int, Guid, Guid, Guid, Guid>, FSharpChoiceCopier<int, Guid, Guid, Guid, Guid>>(output)
    {
        protected override FSharpChoice<int, Guid, Guid, Guid, Guid>[] TestValues => [FSharpChoice<int, Guid, Guid, Guid, Guid>.NewChoice1Of5(-1), FSharpChoice<int, Guid, Guid, Guid, Guid>.NewChoice5Of5(Guid.NewGuid())];

        protected override FSharpChoice<int, Guid, Guid, Guid, Guid> CreateValue() => FSharpChoice<int, Guid, Guid, Guid, Guid>.NewChoice1Of5(0);
    }

    public class FSharpChoice6Tests(ITestOutputHelper output) : FieldCodecTester<FSharpChoice<int, Guid, Guid, Guid, Guid, Guid>, FSharpChoiceCodec<int, Guid, Guid, Guid, Guid, Guid>>(output)
    {
        protected override FSharpChoice<int, Guid, Guid, Guid, Guid, Guid>[] TestValues => [FSharpChoice<int, Guid, Guid, Guid, Guid, Guid>.NewChoice1Of6(-1), FSharpChoice<int, Guid, Guid, Guid, Guid, Guid>.NewChoice6Of6(Guid.NewGuid())];

        protected override FSharpChoice<int, Guid, Guid, Guid, Guid, Guid> CreateValue() => FSharpChoice<int, Guid, Guid, Guid, Guid, Guid>.NewChoice1Of6(0);
    }

    public class FSharpChoice6CopierTests(ITestOutputHelper output) : CopierTester<FSharpChoice<int, Guid, Guid, Guid, Guid, Guid>, FSharpChoiceCopier<int, Guid, Guid, Guid, Guid, Guid>>(output)
    {
        protected override FSharpChoice<int, Guid, Guid, Guid, Guid, Guid>[] TestValues => [FSharpChoice<int, Guid, Guid, Guid, Guid, Guid>.NewChoice1Of6(-1), FSharpChoice<int, Guid, Guid, Guid, Guid, Guid>.NewChoice6Of6(Guid.NewGuid())];

        protected override FSharpChoice<int, Guid, Guid, Guid, Guid, Guid> CreateValue() => FSharpChoice<int, Guid, Guid, Guid, Guid, Guid>.NewChoice1Of6(0);
    }

    public class FSharpSetTests(ITestOutputHelper output) : FieldCodecTester<FSharpSet<string>, FSharpSetCodec<string>>(output)
    {
        protected override FSharpSet<string> CreateValue()
        {
            var hashSet = new HashSet<string>();
            var len = Random.Next(17);
            for (var i = 0; i < len + 5; i++)
            {
                _ = hashSet.Add(Random.Next().ToString());
            }

            return SetModule.OfSeq(hashSet);
        }

        protected override FSharpSet<string>[] TestValues => [null, SetModule.Empty<string>(), new FSharpSet<string>(Array.Empty<string>()), CreateValue(), CreateValue(), CreateValue()];

        protected override bool Equals(FSharpSet<string> left, FSharpSet<string> right) => ReferenceEquals(left, right) || left.SequenceEqual(right);
    }

    public class FSharpSetCopierTests(ITestOutputHelper output) : CopierTester<FSharpSet<string>, FSharpSetCopier<string>>(output)
    {
        protected override FSharpSet<string> CreateValue()
        {
            var hashSet = new HashSet<string>();
            var len = Random.Next(17);
            for (var i = 0; i < len + 5; i++)
            {
                _ = hashSet.Add(Random.Next().ToString());
            }

            return SetModule.OfSeq(hashSet);
        }

        protected override FSharpSet<string>[] TestValues => [null, SetModule.Empty<string>(), new FSharpSet<string>(Array.Empty<string>()), CreateValue(), CreateValue(), CreateValue()];

        protected override bool Equals(FSharpSet<string> left, FSharpSet<string> right) => ReferenceEquals(left, right) || left.SequenceEqual(right);
    }

    public class FSharpMapTests(ITestOutputHelper output) : FieldCodecTester<FSharpMap<string, string>, FSharpMapCodec<string, string>>(output)
    {
        protected override int[] MaxSegmentSizes => [128];

        protected override FSharpMap<string, string> CreateValue()
        {
            var collection = new List<Tuple<string, string>>();
            var len = Random.Next(17);
            for (var i = 0; i < len + 5; i++)
            {
                collection.Add(Tuple.Create(Guid.NewGuid().ToString(), Guid.NewGuid().ToString()));
            }

            return MapModule.OfSeq(collection);
        }

        protected override FSharpMap<string, string>[] TestValues => [null, MapModule.Empty<string, string>(), new FSharpMap<string, string>(Array.Empty<Tuple<string, string>>()), CreateValue(), CreateValue(), CreateValue()];

        protected override bool Equals(FSharpMap<string, string> left, FSharpMap<string, string> right) => ReferenceEquals(left, right) || left.SequenceEqual(right);
    }

    public class FSharpMapCopierTests(ITestOutputHelper output) : CopierTester<FSharpMap<string, string>, FSharpMapCopier<string, string>>(output)
    {
        protected override FSharpMap<string, string> CreateValue()
        {
            var collection = new List<Tuple<string, string>>();
            var len = Random.Next(17);
            for (var i = 0; i < len + 5; i++)
            {
                collection.Add(Tuple.Create(Guid.NewGuid().ToString(), Guid.NewGuid().ToString()));
            }

            return MapModule.OfSeq(collection);
        }

        protected override FSharpMap<string, string>[] TestValues => [null, MapModule.Empty<string, string>(), new FSharpMap<string, string>(Array.Empty<Tuple<string, string>>()), CreateValue(), CreateValue(), CreateValue()];

        protected override bool Equals(FSharpMap<string, string> left, FSharpMap<string, string> right) => ReferenceEquals(left, right) || left.SequenceEqual(right);
    }

    public class FSharpListTests(ITestOutputHelper output) : FieldCodecTester<FSharpList<string>, FSharpListCodec<string>>(output)
    {
        protected override FSharpList<string> CreateValue()
        {
            var list = new List<string>();
            var len = Random.Next(17);
            for (var i = 0; i < len + 5; i++)
            {
                list.Add(Random.Next().ToString());
            }

            return ListModule.OfSeq(list);
        }

        protected override FSharpList<string>[] TestValues => [null, ListModule.Empty<string>(), CreateValue(), CreateValue(), CreateValue()];

        protected override bool Equals(FSharpList<string> left, FSharpList<string> right) => ReferenceEquals(left, right) || left.SequenceEqual(right);
    }

    public class FSharpListCopierTests(ITestOutputHelper output) : CopierTester<FSharpList<string>, FSharpListCopier<string>>(output)
    {
        protected override FSharpList<string> CreateValue()
        {
            var list = new List<string>();
            var len = Random.Next(17);
            for (var i = 0; i < len + 5; i++)
            {
                list.Add(Random.Next().ToString());
            }

            return ListModule.OfSeq(list);
        }

        protected override FSharpList<string>[] TestValues => [null, ListModule.Empty<string>(), CreateValue(), CreateValue(), CreateValue()];

        protected override bool Equals(FSharpList<string> left, FSharpList<string> right) => ReferenceEquals(left, right) || left.SequenceEqual(right);
    }

    public class CultureInfoTests(ITestOutputHelper output) : FieldCodecTester<CultureInfo, CultureInfoCodec>(output)
    {
        protected override CultureInfo CreateValue() => TestValues[Random.Next(TestValues.Length)];

        protected override CultureInfo[] TestValues => [null, CultureInfo.CurrentCulture, CultureInfo.InvariantCulture, CultureInfo.GetCultureInfo("en-us"), CultureInfo.GetCultureInfo("mn-MN")];

        protected override bool Equals(CultureInfo left, CultureInfo right) => ReferenceEquals(left, right) || left.Equals(right);
    }

    public class CultureInfoCopierTests(ITestOutputHelper output) : CopierTester<CultureInfo, IDeepCopier<CultureInfo>>(output)
    {
        protected override bool IsImmutable => true;
        protected override CultureInfo CreateValue() => TestValues[Random.Next(TestValues.Length)];

        protected override CultureInfo[] TestValues => [null, CultureInfo.CurrentCulture, CultureInfo.InvariantCulture, CultureInfo.GetCultureInfo("en-us"), CultureInfo.GetCultureInfo("mn-MN")];

        protected override bool Equals(CultureInfo left, CultureInfo right) => ReferenceEquals(left, right) || left.Equals(right);
    }

    public class CompareInfoTests(ITestOutputHelper output) : FieldCodecTester<CompareInfo, CompareInfoCodec>(output)
    {
        protected override CompareInfo CreateValue() => TestValues[Random.Next(TestValues.Length)];

        protected override CompareInfo[] TestValues => [null, CultureInfo.CurrentCulture.CompareInfo, CultureInfo.InvariantCulture.CompareInfo, CompareInfo.GetCompareInfo("en-us"), CompareInfo.GetCompareInfo("mn-MN")];

        protected override bool Equals(CompareInfo left, CompareInfo right) => ReferenceEquals(left, right) || left.Equals(right);
    }

    public class CompareInfoCopierTests(ITestOutputHelper output) : CopierTester<CompareInfo, IDeepCopier<CompareInfo>>(output)
    {
        protected override bool IsImmutable => true;
        protected override CompareInfo CreateValue() => TestValues[Random.Next(TestValues.Length)];

        protected override CompareInfo[] TestValues => [null, CultureInfo.CurrentCulture.CompareInfo, CultureInfo.InvariantCulture.CompareInfo, CompareInfo.GetCompareInfo("en-us"), CompareInfo.GetCompareInfo("mn-MN")];

        protected override bool Equals(CompareInfo left, CompareInfo right) => ReferenceEquals(left, right) || left.Equals(right);
    }

    public class ResponseCodecTests(ITestOutputHelper output) : FieldCodecTester<Response, IFieldCodec<Response>>(output)
    {
        protected override Response CreateValue() => Response.FromResult(Guid.NewGuid());

        protected override Response[] TestValues =>
        [
            default,
            Response.FromResult(156),
            Response.FromResult("hello"),
            Response.FromException(new Exception("uhoh")),
        ];

        protected override bool Equals(Response left, Response right)
        {
            if (left is null && right is null)
            {
                return true;
            }
            else if (left is null || right is null)
            {
                return false;
            }

            if (left.Exception is not null)
            {
                if (right.Exception is null)
                {
                    return false;
                }

                return string.Equals(left.Exception.Message, right.Exception.Message, StringComparison.Ordinal);
            }

            if (left.Result is null && right.Result is null)
            {
                return true;
            }
            else if (left.Result is null || right.Result is null)
            {
                return false;
            }

            return Equals(left.Result, right.Result);
        }
    }

    public class ResponseCopierTests(ITestOutputHelper output) : CopierTester<Response, IDeepCopier<Response>>(output)
    {
        protected override bool IsImmutable => true;
        protected override bool IsPooled => true;

        protected override Response CreateValue() => Response.FromResult(Guid.NewGuid().ToString());

        protected override Response[] TestValues =>
        [
            default,
            Response.Completed,
            Response.FromResult(156),
            Response.FromResult("hello"),
            Response.FromException(new Exception("uhoh")),
        ];

        protected override bool Equals(Response left, Response right)
        {
            if (left is null && right is null)
            {
                return true;
            }
            else if (left is null || right is null)
            {
                return false;
            }

            if (left.Exception is not null)
            {
                if (right.Exception is null)
                {
                    return false;
                }

                return string.Equals(left.Exception.Message, right.Exception.Message, StringComparison.Ordinal);
            }

            if (left.Result is null && right.Result is null)
            {
                return true;
            }
            else if (left.Result is null || right.Result is null)
            {
                return false;
            }

            return Equals(left.Result, right.Result);
        }
    }

    public class Response1CodecTests(ITestOutputHelper output) : FieldCodecTester<Response<int>, IFieldCodec<Response<int>>>(output)
    {
        protected override Response<int> CreateValue() => (Response<int>)Response.FromResult(Random.Next());

        protected override Response<int>[] TestValues =>
        [
            default,
            (Response<int>)Response.FromResult(156),
        ];

        protected override bool Equals(Response<int> left, Response<int> right)
        {
            if (left is null && right is null)
            {
                return true;
            }
            else if (left is null || right is null)
            {
                return false;
            }

            if (left.Exception is not null)
            {
                if (right.Exception is null)
                {
                    return false;
                }

                return string.Equals(left.Exception.Message, right.Exception.Message, StringComparison.Ordinal);
            }

            if (left.Result is null && right.Result is null)
            {
                return true;
            }
            else if (left.Result is null || right.Result is null)
            {
                return false;
            }

            return Equals(left.Result, right.Result);
        }
    }

    public class Response1CopierTests(ITestOutputHelper output) : CopierTester<Response<int>, IDeepCopier<Response<int>>>(output)
    {
        protected override bool IsImmutable => true;
        protected override bool IsPooled => true;

        protected override Response<int> CreateValue() => (Response<int>)Response.FromResult(Random.Next());

        protected override Response<int>[] TestValues =>
        [
            default,
            (Response<int>)Response.FromResult(156),
        ];

        protected override bool Equals(Response<int> left, Response<int> right)
        {
            if (left is null && right is null)
            {
                return true;
            }
            else if (left is null || right is null)
            {
                return false;
            }

            if (left.Exception is not null)
            {
                if (right.Exception is null)
                {
                    return false;
                }

                return string.Equals(left.Exception.Message, right.Exception.Message, StringComparison.Ordinal);
            }

            if (left.Result is null && right.Result is null)
            {
                return true;
            }
            else if (left.Result is null || right.Result is null)
            {
                return false;
            }

            return Equals(left.Result, right.Result);
        }
    }

    public class ExceptionCodecTests(ITestOutputHelper output) : FieldCodecTester<Exception, ExceptionCodec>(output)
    {
        protected override Exception[] TestValues => [null, new Exception("hi"), CreateValue()];
        protected override int[] MaxSegmentSizes => [8096];

        protected override Exception CreateValue()
        {
            try
            {
                throw new InvalidOperationException("ExpectedException");
            }
            catch (Exception ex)
            {
                return ex;
            }
        }

        protected override bool Equals(Exception left, Exception right)
        {
            // Validation for exceptions is light. They are generally transformed in some fashion as they are serialized and deserialized.
            if (left is null && right is null)
            {
                return true;
            }
            else if (left is null || right is null)
            {
                return false;
            }

            return string.Equals(left.Message, right.Message, StringComparison.Ordinal);
        }
    }

    public class ExceptionCopierTests(ITestOutputHelper output) : CopierTester<Exception, IDeepCopier<Exception>>(output)
    {
        protected override bool IsImmutable => true;
        protected override Exception[] TestValues => [null, new Exception("hi"), CreateValue()];

        protected override Exception CreateValue()
        {
            try
            {
                throw new InvalidOperationException("ExpectedException");
            }
            catch (Exception ex)
            {
                return ex;
            }
        }

        protected override bool Equals(Exception left, Exception right)
        {
            // Validation for exceptions is light. They are generally transformed in some fashion as they are serialized and deserialized.
            if (left is null && right is null)
            {
                return true;
            }
            else if (left is null || right is null)
            {
                return false;
            }

            return string.Equals(left.Message, right.Message, StringComparison.Ordinal);
        }
    }

    public class AggregateExceptionCodecTests(ITestOutputHelper output) : FieldCodecTester<AggregateException, IFieldCodec<AggregateException>>(output)
    {
        protected override AggregateException[] TestValues => [null, new AggregateException("hi"), CreateValue()];
        protected override int[] MaxSegmentSizes => [8096];

        protected override AggregateException CreateValue()
        {
            try
            {
                try
                {
                    throw new InvalidOperationException("ExpectedAggregateException");
                }
                catch (Exception ex)
                {
                    throw new AggregateException("Aahh", ex, new InvalidCastException("Boo!"));
                }
            }
            catch (AggregateException ag)
            {
                return ag;
            }
        }

        protected override bool Equals(AggregateException left, AggregateException right)
        {
            // Validation for exceptions is light. They are generally transformed in some fashion as they are serialized and deserialized.
            if (left is null && right is null)
            {
                return true;
            }
            else if (left is null || right is null)
            {
                return false;
            }

            return string.Equals(left.Message, right.Message, StringComparison.Ordinal);
        }
    }

    public class AggregateExceptionCopierTests(ITestOutputHelper output) : CopierTester<AggregateException, IDeepCopier<AggregateException>>(output)
    {
        protected override bool IsImmutable => true;
        protected override AggregateException[] TestValues => [null, new AggregateException("hi"), CreateValue()];

        protected override AggregateException CreateValue()
        {
            try
            {
                try
                {
                    throw new InvalidOperationException("ExpectedAggregateException");
                }
                catch (Exception ex)
                {
                    throw new AggregateException("Aahh", ex, new InvalidCastException("Boo!"));
                }
            }
            catch (AggregateException ag)
            {
                return ag;
            }
        }

        protected override bool Equals(AggregateException left, AggregateException right)
        {
            // Validation for exceptions is light. They are generally transformed in some fashion as they are serialized and deserialized.
            if (left is null && right is null)
            {
                return true;
            }
            else if (left is null || right is null)
            {
                return false;
            }

            if (left.InnerExceptions.Count != right.InnerExceptions.Count)
            {
                return false;
            }

            foreach (var inner in left.InnerExceptions.Zip(right.InnerExceptions))
            {
                if (!string.Equals(inner.First.Message, inner.Second.Message, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return string.Equals(left.Message, right.Message, StringComparison.Ordinal);
        }
    }
}