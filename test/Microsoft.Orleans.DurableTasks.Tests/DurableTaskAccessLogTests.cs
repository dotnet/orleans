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

            // Other code replaces the item with an equal one, and the text with the same string.
            f.Outside(static f =>
            {
                f.Item.Value = new AccessItem("b");
                f.Text.Value = "x";
            });
            observed.Add(f.Item.Value);
            observed.Add(f.Text.Value);
        });
        log.Flush();

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
    public void ReadOfAnUnchangedKeyRepeatsAcrossWritesToOtherKeys()
    {
        var f = new AccessLogFixture();
        f.Outside(static f => f.Dictionary["a"] = "value-of-a-which-is-long-enough");
        var one = Measure(f, 1);
        var eleven = Measure(f, 11);

        // Each further iteration records a write without a result, with its arguments' hash, and a reference, not the read's value.
        Assert.True((eleven - one) / 10 < 16, $"Each iteration recorded {(eleven - one) / 10} bytes.");

        static int Measure(AccessLogFixture f, int iterations)
        {
            var storage = new MemoryAccessLogStorage();
            var log = f.CreateLog(storage);
            f.Run(log, f =>
            {
                for (var i = 0; i < iterations; i++)
                {
                    Assert.True(f.Dictionary.TryGetValue("a", out _));
                    f.Dictionary["b"] = $"{i}";
                }
            });
            log.Flush();
            return storage.Length;
        }
    }

    [Fact]
    public void ReplayedRepeatsShareTheReadsInstanceAndCopiesStayDistinct()
    {
        var f = new AccessLogFixture();
        f.Item.Value = new AccessItem("a");
        f.Outside(static f => f.Dictionary["k"] = "v");
        f.Record(static f =>
        {
            _ = f.Item.Value;
            _ = f.Item.Value;
            _ = f.Item.Value;
            _ = f.Dictionary.Keys;
            _ = f.Dictionary.Keys;
        });

        var replay = f.CreateLog();
        f.Run(replay, static f =>
        {
            var first = f.Item.Value;
            Assert.Same(first, f.Item.Value);
            Assert.Same(first, f.Item.Value);

            // Each read of the keys returns a copy, as each live read does.
            var keys = f.Dictionary.Keys;
            var again = f.Dictionary.Keys;
            Assert.NotSame(keys, again);
            Assert.Equal(["k"], again);
        });
        Assert.False(replay.IsReplaying);
    }

    [Fact]
    public void ReplayedRepeatsShareTheObjectsWhichLiveRepeatsShare()
    {
        var f = new AccessLogFixture();
        f.Outside(static f => f.Boxes["k"] = new AccessBox());
        f.Record(Body);

        f.Outside(static f => f.Boxes["k"] = new AccessBox { N = 10 });
        var replay = f.CreateLog();
        f.Run(replay, Body);
        Assert.False(replay.IsReplaying);

        // The body changes a value in place, which later repeats observe, live and replayed.
        static void Body(AccessLogFixture f)
        {
            Assert.True(f.Boxes.TryGetValue("k", out var first));
            first.N++;
            Assert.True(f.Boxes.TryGetValue("k", out var second));
            Assert.Same(first, second);
            Assert.Equal(1, second.N);

            var values = f.Boxes.Values;
            var again = f.Boxes.Values;
            Assert.NotSame(values, again);
            Assert.Same(Assert.Single(values), Assert.Single(again));
        }
    }

    [Fact]
    public void ReplayedReadsDontStandForLaterReads()
    {
        var f = new AccessLogFixture();
        f.Item.Value = new AccessItem("a");
        f.Record(static f => _ = f.Item.Value);

        // The next attempt replays the read, then reads again after other code changed the item.
        f.Outside(static f => f.Item.Value = new AccessItem("b"));
        var second = f.CreateLog();
        f.Run(second, static f =>
        {
            Assert.Equal(new AccessItem("a"), f.Item.Value);
            Assert.Equal(new AccessItem("b"), f.Item.Value);
        });
        second.Flush();
        second.Close();

        f.Outside(static f => f.Item.Value = new AccessItem("c"));
        var third = f.CreateLog();
        f.Run(third, static f =>
        {
            Assert.Equal(new AccessItem("a"), f.Item.Value);
            Assert.Equal(new AccessItem("b"), f.Item.Value);
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
    [InlineData(0xC1)] // A reserved bit.
    [InlineData(0x89)] // An exception without a value.
    [InlineData(0xA5)] // A reference with a value.
    public void RecordWhichThisVersionCantReadDiverges(byte header)
    {
        var f = new AccessLogFixture();
        f.Storage.AppendAccessLog(AccessLogFixture.TaskId, [header, (byte)JournaledStateOperation.ValueGet, 0, .. BitConverter.GetBytes(f.TextId)]);
        var replay = f.CreateLog();
        var divergence = Assert.Throws<DurableTaskDivergenceException>(() => f.Run(replay, static f => _ = f.Text.Value));
        Assert.Contains("representation", divergence.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RepeatOfAReadWhichTheLogDoesntHoldDiverges()
    {
        var f = new AccessLogFixture();
        f.Storage.AppendAccessLog(AccessLogFixture.TaskId, [0xA1, (byte)JournaledStateOperation.ValueGet, 0, .. BitConverter.GetBytes(f.TextId), 1]);
        var replay = f.CreateLog();
        var divergence = Assert.Throws<DurableTaskDivergenceException>(() => f.Run(replay, static f => _ = f.Text.Value));
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
        Assert.Throws<DurableTaskDivergenceException>(() => f.Run(replay, static f =>
        {
            _ = f.Text.Value;
            _ = f.Item.Value;
        }));
    }

    /// <summary>
    /// Runs random bodies over several attempts, with other code changing the state between the body's accesses and with
    /// attempts which end before they stage their reads, and checks that every replayed access returns what the access
    /// observed when it ran live.
    /// </summary>
    [Fact]
    public void ReplaysReturnWhatTheRecordingAttemptsObserved()
    {
        var step = Gen.OneOf(
            Gen.Int[0, 7].Select(static kind => new Step(StepKind.Read, kind, Key: 0, Value: 0)),
            Gen.Select(Gen.Int[0, 7], Gen.Int[0, 2], static (kind, key) => new Step(StepKind.Read, kind, key, 0)),
            Gen.Select(Gen.Int[0, 4], Gen.Int[0, 2], Gen.Int[0, 2], static (kind, key, value) => new Step(StepKind.Write, kind, key, value)),
            Gen.Select(Gen.Int[0, 4], Gen.Int[0, 2], Gen.Int[0, 2], static (kind, key, value) => new Step(StepKind.Outside, kind, key, value)),
            Gen.Const(new Step(StepKind.Flush, 0, 0, 0)));
        var scenario = Gen.Select(step.Array[1, 40], Gen.Int[0, 40].Array[0, 3]);
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

    private enum StepKind
    {
        Read,
        Write,
        Outside,
        Flush,
    }

    private readonly record struct Step(StepKind Kind, int Operation, int Key, int Value);

    /// <summary>Journaled states whose accesses run through the access log of the attempt which runs.</summary>
    private sealed class AccessLogFixture
    {
        public static readonly TaskId TaskId = TaskId.CreateRoot("access-log");

        private static readonly string[] Keys = ["a", "b", "c"];
        private static readonly string[] Values = ["x", "y", "z"];

        private readonly Serializer _serializer;
        private readonly AccessLogProvider _provider = new();
        private readonly JournalBufferWriter _journal;

        public AccessLogFixture()
        {
            var collection = new ServiceCollection().AddSerializer();
            collection.AddSingleton<OrleansBinaryJournalFormat>();
            collection.AddKeyedSingleton<IJournalFormat>(OrleansBinaryJournalFormat.JournalFormatKey, static (sp, _) => sp.GetRequiredService<OrleansBinaryJournalFormat>());
            collection.AddKeyedSingleton(typeof(IDurableDictionaryCommandCodec<,>), OrleansBinaryJournalFormat.JournalFormatKey, typeof(OrleansBinaryDurableDictionaryCommandCodec<,>));
            var services = collection.BuildServiceProvider();
            _serializer = services.GetRequiredService<Serializer>();
            var codecs = services.GetRequiredService<ICodecProvider>();
            var sessions = services.GetRequiredService<SerializerSessionPool>();
            var shared = new JournaledStateManagerShared(
                NullLogger<JournaledStateManager>.Instance,
                Options.Create(new JournaledStateManagerOptions { JournalFormatKey = OrleansBinaryJournalFormat.JournalFormatKey }),
                TimeProvider.System,
                services);
            var manager = new JournaledStateManager(shared, new VolatileJournalStorage(OrleansBinaryJournalFormat.JournalFormatKey)) { ContextProvider = _provider };
            Text = new DurableValue<string>("text", manager, new OrleansBinaryDurableValueCommandCodec<string>(codecs.GetCodec<string>(), sessions));
            Item = new DurableValue<AccessItem>("item", manager, new OrleansBinaryDurableValueCommandCodec<AccessItem>(codecs.GetCodec<AccessItem>(), sessions));
            Dictionary = new DurableDictionary<string, string>(
                "dictionary",
                manager,
                new OrleansBinaryDurableDictionaryCommandCodec<string, string>(codecs.GetCodec<string>(), codecs.GetCodec<string>(), sessions));
            _journal = ((IJournalFormat)new OrleansBinaryJournalFormat(sessions)).CreateWriter();
            Boxes = new DurableDictionary<string, AccessBox>(
                "boxes",
                manager,
                new OrleansBinaryDurableDictionaryCommandCodec<string, AccessBox>(codecs.GetCodec<string>(), codecs.GetCodec<AccessBox>(), sessions));
            ((IStateMachine)Boxes).Reset(_journal.CreateJournalStreamWriter(new JournalStreamId(4)));
            ((IStateMachine)Text).Reset(_journal.CreateJournalStreamWriter(new JournalStreamId(1)));
            ((IStateMachine)Item).Reset(_journal.CreateJournalStreamWriter(new JournalStreamId(2)));
            ((IStateMachine)Dictionary).Reset(_journal.CreateJournalStreamWriter(new JournalStreamId(3)));
            TextId = new JournaledStateAccessTarget("text", Substitute.For<IJournaledStateManager>()).Id;
        }

        public MemoryAccessLogStorage Storage { get; } = new();

        public DurableValue<string> Text { get; }

        public DurableValue<AccessItem> Item { get; }

        public DurableDictionary<string, string> Dictionary { get; }

        public DurableDictionary<string, AccessBox> Boxes { get; }

        public int TextId { get; }

        public DurableTaskAccessLog CreateLog(MemoryAccessLogStorage? storage = null) =>
            new(new TestDurableTaskAttempt(TaskId), storage ?? Storage, _serializer, TaskScheduler.Current, static exception => exception);

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

        public object? RunStep(DurableTaskAccessLog log, Step step)
        {
            switch (step.Kind)
            {
                case StepKind.Flush:
                    log.Flush();
                    return null;
                case StepKind.Outside:
                    try
                    {
                        Write(step);
                    }
                    catch (ArgumentException)
                    {
                        // Other code adds a key which exists.
                    }

                    return null;
                default:
                    _provider.Log = log;
                    try
                    {
                        return step.Kind is StepKind.Read ? Read(step) : Write(step);
                    }
                    catch (Exception exception)
                    {
                        return exception.GetType();
                    }
                    finally
                    {
                        _provider.Log = null;
                    }
            }
        }

        private object? Read(Step step) => step.Operation switch
        {
            0 => Text.Value,
            1 => Item.Value,
            2 => Dictionary.TryGetValue(Keys[step.Key], out var value) ? value : "(none)",
            3 => Dictionary[Keys[step.Key]],
            4 => Dictionary.ContainsKey(Keys[step.Key]),
            5 => Dictionary.Count,
            6 => string.Join(",", Dictionary.Keys.Order()),
            _ => string.Join(",", Dictionary.Select(static pair => $"{pair.Key}={pair.Value}").Order()),
        };

        private object? Write(Step step)
        {
            switch (step.Operation)
            {
                case 0:
                    Text.Value = Values[step.Value];
                    return null;
                case 1:
                    Item.Value = new AccessItem(Values[step.Value]);
                    return null;
                case 2:
                    Dictionary[Keys[step.Key]] = Values[step.Value];
                    return null;
                case 3:
                    return Dictionary.Remove(Keys[step.Key]);
                default:
                    Dictionary.Add(Keys[step.Key], Values[step.Value]);
                    return null;
            }
        }

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
