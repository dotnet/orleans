using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Orleans.Runtime;
using Orleans.Runtime.GrainDirectory;
using Xunit;

namespace NonSilo.Tests.Utilities;

[TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Runtime")]
public sealed class ActivationDirectoryTests : IDisposable
{
    private readonly ServiceProvider _services;
    private readonly OrleansInstruments _instruments;
    private readonly ActivationDirectory _directory;

    public ActivationDirectoryTests()
    {
        _services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        _instruments = new OrleansInstruments(_services.GetRequiredService<IMeterFactory>());
        _directory = new ActivationDirectory(new CatalogInstruments(_instruments));
    }

    [Fact]
    public void RecordNewTarget_DuplicateGrainIdPreservesOriginalIdentityAndCount()
    {
        var target = Target("original");
        var duplicate = Target("original");
        var neighbor = Target("neighbor");
        Assert.Null(_directory.FindTarget(target.GrainId));
        Assert.Equal(0, _directory.Count);

        _directory.RecordNewTarget(target);
        _directory.RecordNewTarget(target);
        _directory.RecordNewTarget(duplicate);
        Assert.Same(target, _directory.FindTarget(target.GrainId));
        Assert.Equal(1, _directory.Count);
        Assert.False(_directory.RemoveTarget(duplicate));
        _directory.RecordNewTarget(neighbor);
        AssertContents([target, neighbor]);
        Assert.Same(target, _directory.FindTarget(target.GrainId));
        Assert.Same(neighbor, _directory.FindTarget(neighbor.GrainId));
    }

    [Fact]
    public void RemoveTarget_StaleActivationCannotRemoveReplacementOrDecrementCount()
    {
        var original = Target("replaced");
        var replacement = Target("replaced");
        var neighbor = Target("neighbor");
        Assert.False(_directory.RemoveTarget(original));
        Assert.Equal(0, _directory.Count);
        _directory.RecordNewTarget(original);
        _directory.RecordNewTarget(neighbor);
        Assert.True(_directory.RemoveTarget(original));
        Assert.Null(_directory.FindTarget(original.GrainId));
        AssertContents([neighbor]);
        _directory.RecordNewTarget(replacement);
        Assert.False(_directory.RemoveTarget(original));
        Assert.False(_directory.RemoveTarget(original));
        Assert.Same(replacement, _directory.FindTarget(original.GrainId));
        AssertContents([replacement, neighbor]);

        Assert.True(_directory.RemoveTarget(replacement));
        Assert.False(_directory.RemoveTarget(replacement));
        Assert.Null(_directory.FindTarget(replacement.GrainId));
        AssertContents([neighbor]);
        Assert.True(_directory.RemoveTarget(neighbor));
        AssertContents([]);
    }

    [Theory]
    [InlineData("Empty")]
    [InlineData("Full")]
    [InlineData("Point")]
    [InlineData("Nonwrapped")]
    [InlineData("Wrapped")]
    public void EnumerateRange_UsesStableGrainCoordinateAndRetainsContextIdentity(string scenario)
    {
        var targets = Enumerable.Range(0, 64).Select(index => Target($"range-{index}"))
            .OrderBy(target => target.GrainId.GetUniformHashCode()).ToArray();
        foreach (var target in targets)
        {
            _directory.RecordNewTarget(target);
        }

        var lower = targets[8].GrainId.GetUniformHashCode();
        var upper = targets[40].GrainId.GetUniformHashCode();
        Assert.True(lower < upper);
        var (range, expected) = scenario switch
        {
            "Empty" => (RingRange.Empty, Array.Empty<IGrainContext>()),
            "Full" => (RingRange.Full, targets),
            "Point" => (RingRange.FromPoint(lower), targets.Where(target => Hash(target) == lower).ToArray()),
            "Nonwrapped" => (RingRange.Create(lower, upper), targets.Where(target => Hash(target) > lower && Hash(target) <= upper).ToArray()),
            "Wrapped" => (RingRange.Create(upper, lower), targets.Where(target => Hash(target) > upper || Hash(target) <= lower).ToArray()),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };

        var actual = _directory.EnumerateRange(range).ToArray();
        AssertEntries(expected, actual);
        if (scenario == "Nonwrapped")
        {
            Assert.DoesNotContain(actual, pair => pair.Key == targets[8].GrainId);
            Assert.Same(targets[40], Assert.Single(actual, pair => pair.Key == targets[40].GrainId).Value);
        }
        else if (scenario == "Wrapped")
        {
            Assert.DoesNotContain(actual, pair => pair.Key == targets[40].GrainId);
            Assert.Same(targets[8], Assert.Single(actual, pair => pair.Key == targets[8].GrainId).Value);
        }

        AssertContents(targets);
        static uint Hash(IGrainContext target) => target.GrainId.GetUniformHashCode();
    }

    [Fact]
    public void ActivationCountGauge_TracksSuccessfulRegistrationAndConditionalRemoval()
    {
        var measurements = new List<int>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (ReferenceEquals(instrument.Meter, _instruments.Meter) && instrument.Name == InstrumentNames.CATALOG_ACTIVATION_COUNT)
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<int>((_, value, _, _) => measurements.Add(value));
        listener.Start();
        AssertGauge(0);
        var target = Target("measured");
        var stale = Target("measured");
        _directory.RecordNewTarget(target);
        _directory.RecordNewTarget(stale);
        AssertGauge(1);
        Assert.False(_directory.RemoveTarget(stale));
        Assert.Same(target, _directory.FindTarget(target.GrainId));
        AssertGauge(1);
        Assert.True(_directory.RemoveTarget(target));
        Assert.Null(_directory.FindTarget(target.GrainId));
        AssertGauge(0);

        void AssertGauge(int expected)
        {
            measurements.Clear();
            listener.RecordObservableInstruments();
            Assert.Equal(expected, Assert.Single(measurements));
            Assert.Equal(expected, _directory.Count);
        }
    }

    [Fact]
    public async Task ConcurrentRegistrationRemovalAndReplacement_PreserveIdentityAndCountAtEveryCompletedPhase()
    {
        const int workersCount = 4;
        const int entriesCount = 128;
        var originals = Enumerable.Range(0, entriesCount).Select(index => Target($"concurrent-{index}")).ToArray();
        var replacements = Enumerable.Range(0, entriesCount).Select(index => Target($"concurrent-{index}")).ToArray();
        var extras = Enumerable.Range(0, entriesCount).Select(index => Target($"extra-{index}")).ToArray();
        var originalRemovals = new int[workersCount];
        var finalRemovals = new int[workersCount];
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var phases = new Barrier(workersCount, barrier =>
        {
            switch (barrier.CurrentPhaseNumber)
            {
                case 0:
                case 2:
                case 6:
                    AssertContents([]);
                    break;
                case 1:
                    AssertContents(originals);
                    break;
                case 3:
                case 4:
                    AssertContents(replacements);
                    break;
                case 5:
                    AssertContents(replacements.Where((_, index) => index % 2 == 1).Concat(extras.Where((_, index) => index % 2 == 1)));
                    break;
            }

            if (barrier.CurrentPhaseNumber == 2)
            {
                Assert.Equal(entriesCount, originalRemovals.Sum());
            }
            else if (barrier.CurrentPhaseNumber == 6)
            {
                Assert.Equal(entriesCount, finalRemovals.Sum());
            }
        });

        var workers = Enumerable.Range(0, workersCount).Select(worker => Task.Factory.StartNew(() =>
        {
            try
            {
                Meet("all registration workers armed");
                foreach (var target in originals)
                {
                    _directory.RecordNewTarget(target);
                }

                Meet("competing duplicate registrations completed");
                foreach (var target in originals)
                {
                    if (_directory.RemoveTarget(target))
                    {
                        originalRemovals[worker]++;
                    }
                }

                Meet("competing removals completed");
                foreach (var target in replacements)
                {
                    _directory.RecordNewTarget(target);
                }

                Meet("replacement registrations completed");
                foreach (var stale in originals)
                {
                    Assert.False(_directory.RemoveTarget(stale));
                    _directory.RecordNewTarget(stale);
                }

                Meet("stale removal and registration attempts completed");
                for (var index = worker; index < entriesCount; index += workersCount)
                {
                    if (index % 2 == 0)
                    {
                        Assert.True(_directory.RemoveTarget(replacements[index]));
                    }
                    else
                    {
                        _directory.RecordNewTarget(extras[index]);
                    }
                }

                Meet("disjoint additions and removals completed");
                for (var index = 1; index < entriesCount; index += 2)
                {
                    if (_directory.RemoveTarget(replacements[index]))
                    {
                        finalRemovals[worker]++;
                    }

                    if (_directory.RemoveTarget(extras[index]))
                    {
                        finalRemovals[worker]++;
                    }
                }

                Meet("final competing removals completed");
            }
            catch
            {
                cancellation.Cancel();
                throw;
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();

        await Task.WhenAll(workers);
        Assert.Equal(entriesCount, originalRemovals.Sum());
        Assert.Equal(entriesCount, finalRemovals.Sum());
        AssertContents([]);
        foreach (var target in originals.Concat(extras))
        {
            Assert.Null(_directory.FindTarget(target.GrainId));
            Assert.False(_directory.RemoveTarget(target));
        }

        Assert.Equal(0, _directory.Count);

        void Meet(string description)
            => Assert.True(phases.SignalAndWait(TimeSpan.FromSeconds(30), cancellation.Token),
                $"ActivationDirectory phase {phases.CurrentPhaseNumber}: {description}; remaining workers {phases.ParticipantsRemaining}, count {_directory.Count}.");
    }

    public void Dispose() => _services.Dispose();

    private static IGrainContext Target(string key)
    {
        var target = Substitute.For<IGrainContext>();
        target.GrainId.Returns(GrainId.Create("hash-range-tests", key));
        // IGrainContext is IEquatable<IGrainContext>; use activation identity, not default substitute bool values.
        target.Equals(Arg.Any<IGrainContext>()).Returns(call => ReferenceEquals(target, call.Arg<IGrainContext>()));
        return target;
    }

    private void AssertContents(IEnumerable<IGrainContext> expected)
    {
        var targets = expected.ToArray();
        Assert.Equal(targets.Length, _directory.Count);
        AssertEntries(targets, _directory.ToArray());
        foreach (var target in targets)
        {
            Assert.Same(target, _directory.FindTarget(target.GrainId));
        }
    }

    private static void AssertEntries(IGrainContext[] expected, KeyValuePair<GrainId, IGrainContext>[] actual)
    {
        Assert.Equal(expected.Select(target => target.GrainId.ToString()).Order(StringComparer.Ordinal),
            actual.Select(pair => pair.Key.ToString()).Order(StringComparer.Ordinal));
        foreach (var target in expected)
        {
            Assert.Same(target, Assert.Single(actual, pair => pair.Key == target.GrainId).Value);
        }
    }
}
