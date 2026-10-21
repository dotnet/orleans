using System.Globalization;
using CsCheck;
using NSubstitute;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.DurableTasks;
using Orleans.DurableTasks.Runtime;
using Orleans.Journaling;
using Orleans.Serialization;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.Session;
using Xunit;

namespace Microsoft.Orleans.DurableTasks.Tests;

/// <summary>
/// Verifies the access log's records: a read which repeats an earlier read of the same attempt records a reference
/// instead of its value, and a replay returns the same outcomes as the recording attempt observed.
/// </summary>
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
public sealed class DurableTaskAccessLogTests
{
    private const int None = 0;
    private const int Value = 1;
    private const int Thrown = 2;
    private const int Repeat = 3;

    [Fact]
    public void ReadsOfUnchangedStateRecordOneValueAndOneRun()
    {
        var f = new AccessLogFixture();
        f.Text.Value = "first";
        var single = new MemoryAccessLogStorage();
        var once = f.CreateLog(single);
        f.Run(once, static f => _ = f.Text.Value);
        once.Flush();
        var log = f.CreateLog();
        f.Run(log, static f =>
        {
            for (var i = 0; i < 1_000; i++)
            {
                Assert.Equal("first", f.Text.Value);
            }
        });
        log.Flush();

        // One record holds the value, and one record of five bytes holds a run of 999 repeats.
        Assert.Equal(single.Length + 5, f.Storage.Length);
        Assert.Equal([Read(JournaledStateOperation.ValueGet, Value), Read(JournaledStateOperation.ValueGet, Repeat, 999)], Decode(f.Storage));

        f.Text.Value = "changed";
        var replay = f.CreateLog();
        f.Run(replay, static f =>
        {
            for (var i = 0; i < 1_000; i++)
            {
                Assert.Equal("first", f.Text.Value);
            }
        });
        Assert.False(replay.IsReplaying);
    }

    [Fact]
    public void ReadAfterAnotherChangeRecordsItsValueUnlessTheResultIsEqual()
    {
        var f = new AccessLogFixture();
        var first = new AccessItem("a");
        f.Item.Value = first;
        f.Text.Value = "x";
        var log = f.CreateLog();
        var observed = new List<object?>();
        f.Run(log, f =>
        {
            observed.Add(f.Item.Value);
            observed.Add(f.Text.Value);

            // Other code replaces the item with another one, and the text with the same string.
            f.Outside(static f =>
            {
                f.Item.Value = new AccessItem("b");
                f.Text.Value = "x";
            });
            observed.Add(f.Item.Value);
            observed.Add(f.Text.Value);
        });
        log.Flush();
        Assert.Equal(
            [
                Read(JournaledStateOperation.ValueGet, Value),
                Read(JournaledStateOperation.ValueGet, Value),
                Read(JournaledStateOperation.ValueGet, Value),
                Read(JournaledStateOperation.ValueGet, Repeat, 1),
            ],
            Decode(f.Storage));

        f.Outside(static f => f.Item.Value = new AccessItem("c"));
        var replay = f.CreateLog();
        var replayed = new List<object?>();
        f.Run(replay, f =>
        {
            replayed.Add(f.Item.Value);
            replayed.Add(f.Text.Value);
            replayed.Add(f.Item.Value);
            replayed.Add(f.Text.Value);
        });

        Assert.Equal(observed, replayed);
        Assert.Equal(new AccessItem("b"), replayed[2]);
    }

    [Fact]
    public void ReadWhoseResultIsEqualRepeatsAcrossWritesToOtherKeys()
    {
        var f = new AccessLogFixture();
        f.Outside(static f => f.Dictionary["a"] = "value-of-a");
        f.Record(static f =>
        {
            for (var i = 0; i < 3; i++)
            {
                Assert.True(f.Dictionary.TryGetValue("a", out _));
                f.Dictionary["b"] = $"{i}";
            }
        });

        // The write to "b" changes the dictionary, but the read's result compares exactly and is equal.
        Assert.Equal(
            [
                Read(JournaledStateOperation.DictionaryTryGetValue, Value),
                Write(JournaledStateOperation.DictionarySet, None),
                Read(JournaledStateOperation.DictionaryTryGetValue, Repeat, 1),
                Write(JournaledStateOperation.DictionarySet, None),
                Read(JournaledStateOperation.DictionaryTryGetValue, Repeat, 1),
                Write(JournaledStateOperation.DictionarySet, None),
            ],
            Decode(f.Storage));
    }

    [Fact]
    public void ReadOfAnImmutableResultRecordsItsValueAfterAnyChangeOfItsState()
    {
        var f = new AccessLogFixture();
        f.Outside(static f => f.Doubles["a"] = 1.5);
        f.Record(static f =>
        {
            for (var i = 0; i < 2; i++)
            {
                Assert.Equal(1.5, f.Doubles["a"]);
                f.Doubles["b"] = i;
            }
        });

        // A double doesn't compare exactly, so the read repeats only while the dictionary is unchanged.
        Assert.Equal(
            [
                Read(JournaledStateOperation.DictionaryGet, Value),
                Write(JournaledStateOperation.DictionarySet, None),
                Read(JournaledStateOperation.DictionaryGet, Value),
                Write(JournaledStateOperation.DictionarySet, None),
            ],
            Decode(f.Storage));
    }

    [Fact]
    public void ReadOfAnotherKeyOfAnUnchangedDictionaryRecordsItsValue()
    {
        var f = new AccessLogFixture();
        f.Outside(static f =>
        {
            f.Doubles["a"] = 1.5;
            f.Doubles["b"] = -0.0;
        });
        f.Record(Body);
        Assert.Equal(
            [
                Read(JournaledStateOperation.DictionaryGet, Value),
                Read(JournaledStateOperation.DictionaryGet, Value),
                Read(JournaledStateOperation.DictionaryGet, Repeat, 1),
                Read(JournaledStateOperation.DictionaryGet, Value),
            ],
            Decode(f.Storage));

        f.Outside(static f =>
        {
            f.Doubles["a"] = 2;
            f.Doubles["b"] = 0.0;
        });
        var replay = f.CreateLog();
        f.Run(replay, Body);
        Assert.False(replay.IsReplaying);

        static void Body(AccessLogFixture f)
        {
            Assert.Equal(1.5, f.Doubles["a"]);
            Assert.True(double.IsNegative(f.Doubles["b"]));
            Assert.True(double.IsNegative(f.Doubles["b"]));
            Assert.Equal(1.5, f.Doubles["a"]);
        }
    }

    [Fact]
    public void ReplayedRepeatsOfCopiesReturnDistinctCopies()
    {
        var f = new AccessLogFixture();
        f.Outside(static f => f.Dictionary["k"] = "v");
        f.Record(static f =>
        {
            _ = f.Dictionary.Keys;
            _ = f.Dictionary.Keys;
        });
        Assert.Equal([Read(JournaledStateOperation.DictionaryKeys, Value), Read(JournaledStateOperation.DictionaryKeys, Repeat, 1)], Decode(f.Storage));

        f.Outside(static f => f.Dictionary["other"] = "v");
        var replay = f.CreateLog();
        f.Run(replay, static f =>
        {
            // Each read of the keys returns a copy, as each live read does.
            var keys = f.Dictionary.Keys;
            ((string[])keys)[0] = "changed";
            var again = f.Dictionary.Keys;
            Assert.NotSame(keys, again);
            Assert.Equal(["k"], again);
        });
        Assert.False(replay.IsReplaying);
    }

    /// <summary>
    /// The body changes in place, through one read's result, an object which the state holds and which later reads of
    /// other operations return: a replay returns what each read observed when it ran.
    /// </summary>
    [Fact]
    public void ReplayReturnsWhatEachReadObservedAfterAnInPlaceChangeThroughAnAlias()
    {
        var f = new AccessLogFixture();
        f.Outside(static f => f.Boxes["k"] = new AccessBox());
        var live = new List<int>();
        f.Record(f => Body(f, live));
        Assert.Equal([0, 0, 1, 1, 1], live);
        Assert.All(Decode(f.Storage), static record => Assert.Equal(Value, record.Form));

        f.Outside(static f => f.Boxes["k"] = new AccessBox { N = 10 });
        var replayed = new List<int>();
        var replay = f.CreateLog();
        f.Run(replay, f => Body(f, replayed));
        Assert.False(replay.IsReplaying);
        Assert.Equal(live, replayed);

        static void Body(AccessLogFixture f, List<int> observed)
        {
            Assert.True(f.Boxes.TryGetValue("k", out var x));
            var y = f.Boxes["k"];
            observed.Add(x.N);
            observed.Add(y.N);
            x.N++;
            var z = f.Boxes["k"];
            Assert.True(f.Boxes.TryGetValue("k", out var w));
            observed.Add(z.N);
            observed.Add(w.N);
            observed.Add(Assert.Single(f.Boxes.Values).N);
        }
    }

    [Fact]
    public void ReplayedReadsDontStandForLaterReads()
    {
        var f = new AccessLogFixture();
        f.Outside(static f => f.Doubles["k"] = 1);
        f.Record(static f => _ = f.Doubles["k"]);

        // The next attempt replays the read, then reads again after other code changed the value.
        f.Outside(static f => f.Doubles["k"] = 2);
        var second = f.CreateLog();
        f.Run(second, static f =>
        {
            Assert.Equal(1, f.Doubles["k"]);
            Assert.Equal(2, f.Doubles["k"]);
        });
        second.Flush();
        second.Close();
        Assert.Equal([Read(JournaledStateOperation.DictionaryGet, Value), Read(JournaledStateOperation.DictionaryGet, Value)], Decode(f.Storage));

        f.Outside(static f => f.Doubles["k"] = 3);
        var third = f.CreateLog();
        f.Run(third, static f =>
        {
            Assert.Equal(1, f.Doubles["k"]);
            Assert.Equal(2, f.Doubles["k"]);
        });
        Assert.False(third.IsReplaying);
    }

    [Fact]
    public void LaterAttemptsAddStatesToTheLogsTable()
    {
        var f = new AccessLogFixture();
        f.Text.Value = "t";
        f.Item.Value = new AccessItem("i");
        f.Record(static f => _ = f.Text.Value);

        var second = f.CreateLog();
        f.Run(second, static f =>
        {
            Assert.Equal("t", f.Text.Value);
            Assert.Equal(new AccessItem("i"), f.Item.Value);
            Assert.Equal("t", f.Text.Value);
        });
        second.Flush();
        second.Close();

        f.Outside(static f =>
        {
            f.Text.Value = "changed";
            f.Item.Value = new AccessItem("changed");
        });
        var third = f.CreateLog();
        f.Run(third, static f =>
        {
            Assert.Equal("t", f.Text.Value);
            Assert.Equal(new AccessItem("i"), f.Item.Value);
            Assert.Equal("t", f.Text.Value);
        });
        Assert.False(third.IsReplaying);
    }

    [Fact]
    public void ReadsWhichThrowAreRecordedAndDontInterruptRepeats()
    {
        var f = new AccessLogFixture();
        f.Outside(static f => f.Dictionary["a"] = "x");
        f.Record(static f =>
        {
            Assert.Equal("x", f.Dictionary["a"]);
            Assert.Throws<KeyNotFoundException>(() => f.Dictionary["missing"]);
            Assert.Equal("x", f.Dictionary["a"]);
        });

        f.Outside(static f =>
        {
            f.Dictionary["a"] = "y";
            f.Dictionary["missing"] = "now present";
        });
        var replay = f.CreateLog();
        f.Run(replay, static f =>
        {
            Assert.Equal("x", f.Dictionary["a"]);
            Assert.Throws<KeyNotFoundException>(() => f.Dictionary["missing"]);
            Assert.Equal("x", f.Dictionary["a"]);
        });
        Assert.False(replay.IsReplaying);
    }

    [Theory]
    [InlineData(0x01)] // A record of another format.
    [InlineData(0xC5)] // A position with a value.
    [InlineData(0xE1)] // A position with a reference.
    [InlineData(0x89)] // An exception without a value.
    [InlineData(0xA5)] // A reference with a value.
    public void RecordWhichThisVersionCantReadDiverges(byte header)
    {
        var f = new AccessLogFixture();
        f.Storage.AppendAccessLog(AccessLogFixture.TaskId, [header, (byte)JournaledStateOperation.ValueGet, 0, .. BitConverter.GetBytes(f.TextId)]);
        var replay = f.CreateLog();
        var divergence = AssertDiverges(f, () => f.Run(replay, static f => _ = f.Text.Value));
        Assert.Contains("representation", divergence.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RepeatOfAReadWhichTheLogDoesntHoldDiverges()
    {
        var f = new AccessLogFixture();
        f.Storage.AppendAccessLog(AccessLogFixture.TaskId, [0xA1, (byte)JournaledStateOperation.ValueGet, 0, .. BitConverter.GetBytes(f.TextId), 1]);
        var replay = f.CreateLog();
        var divergence = AssertDiverges(f, () => f.Run(replay, static f => _ = f.Text.Value));
        Assert.Contains("doesn't hold", divergence.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RepeatOfAnotherReadDiverges()
    {
        var f = new AccessLogFixture();
        f.Text.Value = "t";
        f.Record(static f =>
        {
            _ = f.Text.Value;
            _ = f.Text.Value;
        });

        var replay = f.CreateLog();
        AssertDiverges(f, () => f.Run(replay, static f =>
        {
            _ = f.Text.Value;
            _ = f.Item.Value;
        }));
    }

    [Fact]
    public void RepeatWhoseRecordedReadDoesntDecodeAsTheResultDivergesOnce()
    {
        var f = new AccessLogFixture();
        f.Number.Value = 5;
        f.Record(static f =>
        {
            _ = f.Number.Value;
            _ = f.Number.Value;
        });

        // A state of another type with the same name, whose reads the records name alike, repeats the int as a string.
        var twin = f.CreateTwin<string>("number");
        var replay = f.CreateLog();
        var divergence = AssertDiverges(f, () => f.Run(replay, f =>
        {
            Assert.Equal(5, f.Number.Value);
            _ = twin.Value;
        }));
        Assert.IsNotType<DurableTaskAttemptCanceledException>(divergence.InnerException);
        Assert.IsNotType<DurableTaskDivergenceException>(divergence.InnerException);
    }

    [Fact]
    public void LaterRepeatOfARunWhichDoesntStandForTheResultDiverges()
    {
        var f = new AccessLogFixture();
        f.Number.Value = 5;
        f.Record(static f =>
        {
            _ = f.Number.Value;
            _ = f.Number.Value;
            _ = f.Number.Value;
        });

        var twin = f.CreateTwin<string>("number");
        var replay = f.CreateLog();
        var divergence = AssertDiverges(f, () => f.Run(replay, f =>
        {
            Assert.Equal(5, f.Number.Value);
            Assert.Equal(5, f.Number.Value);
            _ = twin.Value;
        }));
        Assert.IsType<InvalidCastException>(divergence.InnerException);
    }

    [Theory]
    [InlineData(typeof(int), true, true)]
    [InlineData(typeof(string), true, true)]
    [InlineData(typeof((bool, string)), true, true)]
    [InlineData(typeof(int?), true, true)]
    [InlineData(typeof(double), false, true)]
    [InlineData(typeof(float), false, true)]
    [InlineData(typeof(decimal), false, true)]
    [InlineData(typeof(DateTime), false, true)]
    [InlineData(typeof(DateTimeOffset), false, true)]
    [InlineData(typeof((bool, double)), false, true)]
    [InlineData(typeof(KeyValuePair<string, int>), false, true)]
    [InlineData(typeof(AccessItem), false, false)]
    [InlineData(typeof(AccessBox), false, false)]
    [InlineData(typeof((bool, AccessBox)), false, false)]
    [InlineData(typeof(string[]), false, false)]
    public void ValueKindsClassifyWhatARecordedValueStandsFor(Type type, bool exact, bool immutable)
    {
        Assert.Equal(exact, ValueKinds.IsExact(type));
        Assert.Equal(immutable, ValueKinds.IsImmutable(type));
    }

    /// <summary>
    /// Runs random bodies in one attempt, with other code changing the state between the body's accesses, and checks that
    /// each access's record takes the form which a model of the stamp rule predicts: a read records a reference exactly
    /// when its result is equal to its operation's latest recorded result and compares exactly, or when its state hasn't
    /// changed since and its argument is equal and its result is immutable.
    /// </summary>
    [Fact]
    public void RecordsTakeTheFormsWhichTheModelPredicts()
    {
        StepGen.Array[1, 40].Sample(static steps => Check(steps), iter: 300);

        static void Check(Step[] steps)
        {
            var f = new AccessLogFixture();
            var log = f.CreateLog();
            var expected = new List<RecordForm>();
            var latest = new Dictionary<(string State, JournaledStateOperation Operation), (int Key, object? Result, bool Changed)>();
            (string State, JournaledStateOperation Operation, int Key)? openRun = null;
            var runCount = 0;
            foreach (var step in steps)
            {
                switch (step.Kind)
                {
                    case StepKind.Flush:
                        log.Flush();
                        EndRun();
                        break;
                    case StepKind.Outside:
                        if (f.Change(null, step).Changed)
                        {
                            MarkChanged(AccessLogFixture.Writes[step.Operation].State);
                        }

                        break;
                    case StepKind.Write:
                        var write = AccessLogFixture.Writes[step.Operation];
                        var (changed, written) = f.Change(log, step);
                        EndRun();
                        expected.Add(Write(write.Operation, written is Exception ? Thrown : write.HasResult ? Value : None));
                        if (changed)
                        {
                            MarkChanged(write.State);
                        }

                        break;
                    default:
                        var read = AccessLogFixture.Reads[step.Operation];
                        var outcome = f.ReadRaw(log, step);
                        if (outcome is Exception)
                        {
                            EndRun();
                            expected.Add(Read(read.Operation, Thrown));
                            break;
                        }

                        var key = (read.State, read.Operation);
                        var repeats = read.Repeats is not Repeats.Never
                            && latest.TryGetValue(key, out var earlier)
                            && (read.Repeats is Repeats.WhenEqual
                                ? Equals(earlier.Result, outcome)
                                : !earlier.Changed && (!read.Keyed || earlier.Key == step.Key));
                        // References with other arguments, such as another key, are separate records.
                        var run = (read.State, read.Operation, read.Keyed ? step.Key : -1);
                        if (repeats)
                        {
                            if (openRun == run)
                            {
                                runCount++;
                            }
                            else
                            {
                                EndRun();
                                openRun = run;
                                runCount = 1;
                            }
                        }
                        else
                        {
                            EndRun();
                            expected.Add(Read(read.Operation, Value));
                            if (read.Repeats is not Repeats.Never)
                            {
                                latest[key] = (step.Key, outcome, false);
                            }
                        }

                        break;
                }
            }

            log.Flush();
            EndRun();
            Assert.Equal(expected, Decode(f.Storage));

            void EndRun()
            {
                if (openRun is { } run)
                {
                    expected.Add(Read(run.Operation, Repeat, runCount));
                    openRun = null;
                }
            }

            void MarkChanged(string state)
            {
                foreach (var key in latest.Keys.ToArray())
                {
                    if (key.State == state)
                    {
                        latest[key] = latest[key] with { Changed = true };
                    }
                }
            }
        }
    }

    /// <summary>
    /// Runs random bodies over several attempts, with other code changing the state between the body's accesses and with
    /// attempts which end before they stage their reads, and checks that every replayed access returns what the access
    /// observed when it ran live.
    /// </summary>
    [Fact]
    public void ReplaysReturnWhatTheRecordingAttemptsObserved()
    {
        var scenario = Gen.Select(StepGen.Array[1, 40], Gen.Int[0, 40].Array[0, 3]);
        scenario.Sample(static (steps, ends) => Check(steps, ends), iter: 300);

        static void Check(Step[] steps, int[] ends)
        {
            var f = new AccessLogFixture();
            var observed = new object?[steps.Length];
            var attemptEnds = ends.Select(end => Math.Min(end, steps.Length)).Order().Append(steps.Length).ToArray();
            foreach (var end in attemptEnds)
            {
                var log = f.CreateLog();
                for (var i = 0; i < end; i++)
                {
                    var replaying = log.IsReplaying;
                    var outcome = f.RunStep(log, steps[i]);
                    if (steps[i].Kind is StepKind.Read or StepKind.Write)
                    {
                        if (replaying)
                        {
                            Assert.Equal(observed[i], outcome);
                        }
                        else
                        {
                            observed[i] = outcome;
                        }
                    }
                }

                if (end == steps.Length)
                {
                    log.Flush();
                }

                log.Close();
            }

            // A final replay returns every observation and records nothing.
            var replay = f.CreateLog();
            for (var i = 0; i < steps.Length; i++)
            {
                var outcome = f.RunStep(replay, steps[i]);
                if (steps[i].Kind is StepKind.Read or StepKind.Write)
                {
                    Assert.Equal(observed[i], outcome);
                }
            }

            Assert.False(replay.IsReplaying);
        }
    }

    private static Gen<Step> StepGen { get; } = Gen.OneOf(
        Gen.Select(Gen.Int[0, AccessLogFixture.Reads.Length - 1], Gen.Int[0, 2], static (kind, key) => new Step(StepKind.Read, kind, key, 0)),
        Gen.Select(Gen.Int[0, AccessLogFixture.Writes.Length - 1], Gen.Int[0, 2], Gen.Int[0, 2], static (kind, key, value) => new Step(StepKind.Write, kind, key, value)),
        Gen.Select(Gen.Int[0, AccessLogFixture.Writes.Length - 1], Gen.Int[0, 2], Gen.Int[0, 2], static (kind, key, value) => new Step(StepKind.Outside, kind, key, value)),
        Gen.Const(new Step(StepKind.Flush, 0, 0, 0)));

    private static DurableTaskDivergenceException AssertDiverges(AccessLogFixture f, Action action)
    {
        var diverged = f.DivergeCount;
        var canceled = Assert.Throws<DurableTaskAttemptCanceledException>(action);
        Assert.Equal(diverged + 1, f.DivergeCount);
        return Assert.IsType<DurableTaskDivergenceException>(canceled.InnerException);
    }

    private static RecordForm Read(JournaledStateOperation operation, int form, int count = 0) => new(JournaledStateAccessKind.Read, operation, form, count);

    private static RecordForm Write(JournaledStateOperation operation, int form) => new(JournaledStateAccessKind.Write, operation, form, 0);

    // Decodes the form of each record which the storage holds, and the count of each run of references.
    private static List<RecordForm> Decode(MemoryAccessLogStorage storage)
    {
        const byte HasValue = 0x04;
        const byte IsException = 0x08;
        const byte HasArgumentHash = 0x10;
        const byte IsReference = 0x20;
        var input = storage.GetAccessLog(AccessLogFixture.TaskId).Span;
        var records = new List<RecordForm>();
        var states = 0;
        var position = 0;
        while (position < input.Length)
        {
            var header = input[position];
            Assert.Equal(0x80, header & 0xC0);
            var operation = (JournaledStateOperation)input[position + 1];
            position += 2;
            if (ReadVarUInt32(input, ref position) == states)
            {
                states++;
                position += 4;
            }

            if ((header & HasArgumentHash) != 0)
            {
                position += 4;
            }

            var form = (header & (HasValue | IsException | IsReference)) switch
            {
                0 => None,
                HasValue => Value,
                HasValue | IsException => Thrown,
                IsReference => Repeat,
                var other => throw new InvalidOperationException($"Form 0x{other:x2}."),
            };
            var count = 0;
            if (form is Value or Thrown)
            {
                var length = ReadVarUInt32(input, ref position);
                position += length;
            }
            else if (form is Repeat)
            {
                count = ReadVarUInt32(input, ref position);
            }

            records.Add(new((JournaledStateAccessKind)(header & 0b11), operation, form, count));
        }

        return records;

        static int ReadVarUInt32(ReadOnlySpan<byte> input, ref int position)
        {
            var value = 0;
            for (var shift = 0; ; shift += 7)
            {
                var next = input[position++];
                value |= (next & 0x7F) << shift;
                if (next < 0x80)
                {
                    return value;
                }
            }
        }
    }

    private enum StepKind
    {
        Read,
        Write,
        Outside,
        Flush,
    }

    private enum Repeats
    {
        Never,
        WhenEqual,
        WhenUnchanged,
    }

    private readonly record struct Step(StepKind Kind, int Operation, int Key, int Value);

    private readonly record struct RecordForm(JournaledStateAccessKind Kind, JournaledStateOperation Operation, int Form, int Count);

    /// <summary>A read of the fixture's state, and when the model predicts that it repeats an earlier read.</summary>
    private sealed record ReadKind(string State, JournaledStateOperation Operation, bool Keyed, Repeats Repeats, Func<AccessLogFixture, int, object?> Run);

    /// <summary>A change of the fixture's state, which returns its result or <see langword="null"/>.</summary>
    private sealed record WriteKind(string State, JournaledStateOperation Operation, bool HasResult, Func<AccessLogFixture, int, int, object?> Run);

    /// <summary>Journaled states whose accesses run through the access log of the attempt which runs.</summary>
    private sealed class AccessLogFixture
    {
        public static readonly TaskId TaskId = TaskId.CreateRoot("access-log");

        private static readonly string[] Keys = ["a", "b", "c"];
        private static readonly string[] Texts = ["x", "y", "z"];
        private static readonly double[] Numbers = [0.0, -0.0, 1.5];

        public static readonly ReadKind[] Reads =
        [
            new("text", JournaledStateOperation.ValueGet, false, Repeats.WhenEqual, static (f, _) => f.Text.Value),
            new("item", JournaledStateOperation.ValueGet, false, Repeats.Never, static (f, _) => f.Item.Value),
            new("dictionary", JournaledStateOperation.DictionaryTryGetValue, true, Repeats.WhenEqual, static (f, k) => f.Dictionary.TryGetValue(Keys[k], out var value) ? (true, value) : (false, null)),
            new("dictionary", JournaledStateOperation.DictionaryGet, true, Repeats.WhenEqual, static (f, k) => f.Dictionary[Keys[k]]),
            new("dictionary", JournaledStateOperation.DictionaryContainsKey, true, Repeats.WhenEqual, static (f, k) => f.Dictionary.ContainsKey(Keys[k])),
            new("dictionary", JournaledStateOperation.DictionaryCount, false, Repeats.WhenEqual, static (f, _) => f.Dictionary.Count),
            new("dictionary", JournaledStateOperation.DictionaryKeys, false, Repeats.WhenUnchanged, static (f, _) => f.Dictionary.Keys),
            new("dictionary", JournaledStateOperation.DictionarySnapshot, false, Repeats.WhenUnchanged, static (f, _) => Enumerate(f.Dictionary)),
            new("boxes", JournaledStateOperation.DictionaryTryGetValue, true, Repeats.Never, static (f, k) => f.Boxes.TryGetValue(Keys[k], out var box) ? (true, box) : (false, null)),
            new("boxes", JournaledStateOperation.DictionaryGet, true, Repeats.Never, static (f, k) => f.Boxes[Keys[k]]),
            new("boxes", JournaledStateOperation.DictionaryValues, false, Repeats.Never, static (f, _) => f.Boxes.Values),
            new("doubles", JournaledStateOperation.DictionaryGet, true, Repeats.WhenUnchanged, static (f, k) => f.Doubles[Keys[k]]),
            new("doubles", JournaledStateOperation.DictionaryTryGetValue, true, Repeats.WhenUnchanged, static (f, k) => f.Doubles.TryGetValue(Keys[k], out var value) ? (true, value) : (false, 0.0)),
            new("doubles", JournaledStateOperation.DictionaryValues, false, Repeats.WhenUnchanged, static (f, _) => f.Doubles.Values),
            new("readings", JournaledStateOperation.ListGet, true, Repeats.WhenUnchanged, static (f, i) => f.Readings[i]),
            new("readings", JournaledStateOperation.ListCount, false, Repeats.WhenEqual, static (f, _) => f.Readings.Count),
            new("samples", JournaledStateOperation.LogGet, true, Repeats.WhenUnchanged, static (f, i) => f.Samples[i]),
            new("samples", JournaledStateOperation.LogTryGet, true, Repeats.WhenUnchanged, static (f, i) => f.Samples.TryGet(i, out var sample) ? (true, sample) : (false, 0.0)),
            new("samples", JournaledStateOperation.LogSnapshot, false, Repeats.WhenUnchanged, static (f, _) => Enumerate(f.Samples)),
        ];

        public static readonly WriteKind[] Writes =
        [
            new("text", JournaledStateOperation.ValueSet, false, static (f, _, v) => f.Text.Value = Texts[v]),
            new("item", JournaledStateOperation.ValueSet, false, static (f, _, v) => f.Item.Value = new AccessItem(Texts[v])),
            new("dictionary", JournaledStateOperation.DictionarySet, false, static (f, k, v) => f.Dictionary[Keys[k]] = Texts[v]),
            new("dictionary", JournaledStateOperation.DictionaryRemove, true, static (f, k, _) => f.Dictionary.Remove(Keys[k])),
            new("dictionary", JournaledStateOperation.DictionaryAdd, false, static (f, k, v) =>
            {
                f.Dictionary.Add(Keys[k], Texts[v]);
                return null;
            }),
            new("boxes", JournaledStateOperation.DictionarySet, false, static (f, k, v) => f.Boxes[Keys[k]] = new AccessBox { N = v }),
            new("doubles", JournaledStateOperation.DictionarySet, false, static (f, k, v) => f.Doubles[Keys[k]] = Numbers[v]),
            new("readings", JournaledStateOperation.ListAdd, false, static (f, _, v) =>
            {
                f.Readings.Add(Numbers[v]);
                return null;
            }),
            new("readings", JournaledStateOperation.ListSet, false, static (f, i, v) => f.Readings[i] = Numbers[v]),
            new("samples", JournaledStateOperation.LogAppend, true, static (f, _, v) => f.Samples.Append(Numbers[v])),
            new("samples", JournaledStateOperation.LogClear, false, static (f, _, _) =>
            {
                f.Samples.Clear();
                return null;
            }),
        ];

        private readonly Serializer _serializer;
        private readonly AccessLogProvider _provider = new();
        private readonly JournalBufferWriter _journal;
        private readonly JournaledStateManagerShared _shared;
        private readonly ICodecProvider _codecs;
        private readonly SerializerSessionPool _sessions;
        private uint _streams;

        public AccessLogFixture()
        {
            var collection = new ServiceCollection().AddSerializer();
            collection.AddSingleton<OrleansBinaryJournalFormat>();
            collection.AddKeyedSingleton<IJournalFormat>(OrleansBinaryJournalFormat.JournalFormatKey, static (sp, _) => sp.GetRequiredService<OrleansBinaryJournalFormat>());
            collection.AddKeyedSingleton(typeof(IDurableDictionaryCommandCodec<,>), OrleansBinaryJournalFormat.JournalFormatKey, typeof(OrleansBinaryDurableDictionaryCommandCodec<,>));
            var services = collection.BuildServiceProvider();
            _serializer = services.GetRequiredService<Serializer>();
            _codecs = services.GetRequiredService<ICodecProvider>();
            _sessions = services.GetRequiredService<SerializerSessionPool>();
            _shared = new JournaledStateManagerShared(
                NullLogger<JournaledStateManager>.Instance,
                Options.Create(new JournaledStateManagerOptions { JournalFormatKey = OrleansBinaryJournalFormat.JournalFormatKey }),
                TimeProvider.System,
                services);
            var manager = CreateManager();
            _journal = ((IJournalFormat)new OrleansBinaryJournalFormat(_sessions)).CreateWriter();
            Text = CreateValue<string>("text", manager);
            Item = CreateValue<AccessItem>("item", manager);
            Number = CreateValue<int>("number", manager);
            Dictionary = CreateDictionary<string>("dictionary", manager);
            Boxes = CreateDictionary<AccessBox>("boxes", manager);
            Doubles = CreateDictionary<double>("doubles", manager);
            Readings = new DurableList<double>("readings", manager, new OrleansBinaryDurableListCommandCodec<double>(_codecs.GetCodec<double>(), _sessions));
            Reset(Readings);
            Samples = new DurableLog<double>("samples", manager, new OrleansBinaryDurableLogCommandCodec<double>(_codecs.GetCodec<double>(), _sessions));
            Reset(Samples);
            TextId = new JournaledStateAccessTarget("text", Substitute.For<IJournaledStateManager>()).Id;
        }

        public MemoryAccessLogStorage Storage { get; } = new();

        public DurableValue<string> Text { get; }

        public DurableValue<AccessItem> Item { get; }

        public DurableValue<int> Number { get; }

        public DurableDictionary<string, string> Dictionary { get; }

        public DurableDictionary<string, AccessBox> Boxes { get; }

        public DurableDictionary<string, double> Doubles { get; }

        public DurableList<double> Readings { get; }

        public DurableLog<double> Samples { get; }

        public int TextId { get; }

        /// <summary>Gets the number of times the logs which this created stalled their attempt as a divergence.</summary>
        public int DivergeCount { get; private set; }

        public DurableTaskAccessLog CreateLog(MemoryAccessLogStorage? storage = null) =>
            new(new TestDurableTaskAttempt(TaskId), storage ?? Storage, _serializer, TaskScheduler.Current, exception =>
            {
                DivergeCount++;
                return new DurableTaskAttemptCanceledException(exception.Message, exception);
            });

        /// <summary>
        /// Creates a value named <paramref name="name"/> in another manager whose accesses run through the same logs, so
        /// records name it as they name the fixture's state of that name.
        /// </summary>
        public DurableValue<T> CreateTwin<T>(string name) => CreateValue<T>(name, CreateManager());

        /// <summary>Runs <paramref name="body"/> in a new attempt, then stages its records and closes it.</summary>
        public void Record(Action<AccessLogFixture> body)
        {
            var log = CreateLog();
            Run(log, body);
            log.Flush();
            log.Close();
        }

        /// <summary>Runs <paramref name="body"/> as the body of the attempt which <paramref name="log"/> records.</summary>
        public void Run(DurableTaskAccessLog log, Action<AccessLogFixture> body)
        {
            _provider.Log = log;
            try
            {
                body(this);
            }
            finally
            {
                _provider.Log = null;
            }
        }

        /// <summary>Runs <paramref name="change"/> as other code of the activation, which no attempt records.</summary>
        public void Outside(Action<AccessLogFixture> change)
        {
            var log = _provider.Log;
            _provider.Log = null;
            try
            {
                change(this);
            }
            finally
            {
                _provider.Log = log;
            }
        }

        /// <summary>Runs a step and returns a description of its outcome, which replays compare.</summary>
        public object? RunStep(DurableTaskAccessLog log, Step step)
        {
            switch (step.Kind)
            {
                case StepKind.Flush:
                    log.Flush();
                    return null;
                case StepKind.Outside:
                    _ = Change(null, step);
                    return null;
                case StepKind.Write:
                    return Describe(Change(log, step).Outcome);
                default:
                    return Describe(ReadRaw(log, step));
            }
        }

        /// <summary>Runs a read in <paramref name="log"/>'s attempt and returns its result or exception.</summary>
        public object? ReadRaw(DurableTaskAccessLog log, Step step)
        {
            _provider.Log = log;
            try
            {
                return Reads[step.Operation].Run(this, step.Key);
            }
            catch (Exception exception)
            {
                return exception;
            }
            finally
            {
                _provider.Log = null;
            }
        }

        /// <summary>
        /// Runs a write in <paramref name="log"/>'s attempt, or as other code, and returns its result or exception and
        /// whether it changed the state: every write which succeeds does, except a removal which removes nothing.
        /// </summary>
        public (bool Changed, object? Outcome) Change(DurableTaskAccessLog? log, Step step)
        {
            _provider.Log = log;
            try
            {
                var result = Writes[step.Operation].Run(this, step.Key, step.Value);
                return (result is not false, Writes[step.Operation].HasResult ? result : null);
            }
            catch (Exception exception)
            {
                return (false, exception);
            }
            finally
            {
                _provider.Log = null;
            }
        }

        private static object? Enumerate<T>(IEnumerable<T> source)
        {
            var items = new List<T>();
            foreach (var item in source)
            {
                items.Add(item);
            }

            return items.ToArray();
        }

        private static string Describe(object? outcome) => outcome switch
        {
            null => "null",
            Exception exception => $"!{exception.GetType().Name}",
            double number => number.ToString("R", CultureInfo.InvariantCulture),
            AccessBox box => $"box:{box.N}",
            KeyValuePair<string, string> pair => $"{pair.Key}={pair.Value}",
            ValueTuple<bool, string> pair => $"({pair.Item1},{Describe(pair.Item2)})",
            ValueTuple<bool, AccessBox> pair => $"({pair.Item1},{Describe(pair.Item2)})",
            ValueTuple<bool, double> pair => $"({pair.Item1},{Describe(pair.Item2)})",
            System.Collections.IEnumerable items and not string => $"[{string.Join(",", items.Cast<object?>().Select(Describe))}]",
            _ => outcome.ToString()!,
        };

        private JournaledStateManager CreateManager() =>
            new(_shared, new VolatileJournalStorage(OrleansBinaryJournalFormat.JournalFormatKey)) { ContextProvider = _provider };

        private DurableValue<T> CreateValue<T>(string name, JournaledStateManager manager)
        {
            var value = new DurableValue<T>(name, manager, new OrleansBinaryDurableValueCommandCodec<T>(_codecs.GetCodec<T>(), _sessions));
            Reset(value);
            return value;
        }

        private DurableDictionary<string, T> CreateDictionary<T>(string name, JournaledStateManager manager)
        {
            var dictionary = new DurableDictionary<string, T>(
                name,
                manager,
                new OrleansBinaryDurableDictionaryCommandCodec<string, T>(_codecs.GetCodec<string>(), _codecs.GetCodec<T>(), _sessions));
            Reset(dictionary);
            return dictionary;
        }

        private void Reset(IStateMachine state) => state.Reset(_journal.CreateJournalStreamWriter(new JournalStreamId(++_streams)));

        private sealed class AccessLogProvider : JournaledStateContextProvider
        {
            public DurableTaskAccessLog? Log;

            public override JournaledStateContext? Current => Log is { IsActive: true } log ? log : null;
        }
    }

    private sealed class MemoryAccessLogStorage : IDurableTaskAccessLogStorage
    {
        private byte[] _log = [];

        public int Length => _log.Length;

        public ReadOnlyMemory<byte> GetAccessLog(TaskId taskId) => _log;

        public void AppendAccessLog(TaskId taskId, ReadOnlySpan<byte> records) => _log = [.. _log, .. records];

        // The tests stage an attempt's reads explicitly, with its log's Flush.
        public void AddPendingRecords(IDurableTaskPendingAccessRecords source)
        {
        }
    }
}

[GenerateSerializer]
internal sealed record AccessItem([property: Id(0)] string Name);

[GenerateSerializer]
internal sealed class AccessBox
{
    [Id(0)]
    public int N { get; set; }
}
