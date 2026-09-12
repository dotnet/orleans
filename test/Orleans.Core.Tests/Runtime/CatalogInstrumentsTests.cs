using System;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Xunit;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace UnitTests.Runtime;

public class CatalogInstrumentsTests
{
    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT"), TestCategory("Runtime")]
    public void ActivationLifecycleLatencyMetrics_AreHistograms()
    {
        var services = new ServiceCollection();
        services.AddMetrics();

        using var serviceProvider = services.BuildServiceProvider();
        var meterFactory = serviceProvider.GetRequiredService<IMeterFactory>();
        var instruments = new CatalogInstruments(new OrleansInstruments(meterFactory));

        Instrument activationLatencyInstrument = null!;
        Instrument deactivationLatencyInstrument = null!;
        var activationLatencyMeasurement = 0d;
        var deactivationLatencyMeasurement = 0d;

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Name is InstrumentNames.CATALOG_ACTIVATION_LATENCY or InstrumentNames.CATALOG_DEACTIVATION_LATENCY)
            {
                meterListener.EnableMeasurementEvents(instrument);
                if (instrument.Name == InstrumentNames.CATALOG_ACTIVATION_LATENCY)
                {
                    activationLatencyInstrument = instrument;
                }
                else
                {
                    deactivationLatencyInstrument = instrument;
                }
            }
        };

        listener.SetMeasurementEventCallback<double>((instrument, measurement, tags, state) =>
        {
            if (instrument.Name == InstrumentNames.CATALOG_ACTIVATION_LATENCY)
            {
                activationLatencyMeasurement = measurement;
            }
            else if (instrument.Name == InstrumentNames.CATALOG_DEACTIVATION_LATENCY)
            {
                deactivationLatencyMeasurement = measurement;
            }
        });

        listener.Start();

        instruments.OnActivationCompleted(TimeSpan.FromMilliseconds(12), CatalogInstruments.ActivationStatusSuccess, usesDirectory: true, grainType: "catalog-grain");
        instruments.OnDeactivationCompleted(TimeSpan.FromMilliseconds(34), CatalogInstruments.DeactivationViaCollection, grainType: "catalog-grain");

        Assert.IsType<Histogram<double>>(activationLatencyInstrument);
        Assert.IsType<Histogram<double>>(deactivationLatencyInstrument);
        Assert.Equal("ms", activationLatencyInstrument.Unit);
        Assert.Equal("ms", deactivationLatencyInstrument.Unit);
        Assert.Equal(12, activationLatencyMeasurement);
        Assert.Equal(34, deactivationLatencyMeasurement);
    }

    [TestSuite("BVT"), TestProvider("None")]
    [Theory, TestCategory("BVT"), TestCategory("Runtime")]
    [InlineData(null, "unknown")]
    [InlineData("orders.é`1[System.Int32]", "orders.é`1[System.Int32]")]
    public void GrainTypeMetrics_DefaultAndTypedMeasurementsHaveStableTags(string? typeName, string expectedName)
    {
        var metrics = new GrainTypeMetrics(typeName is null ? default : GrainType.Create(typeName));
        var activation = metrics.ActivationCount;
        var workingSet = metrics.WorkingSetCount;

        Assert.Equal(expectedName, metrics.GrainTypeTagValue);
        Assert.Equal(0, activation.Value);
        Assert.Equal(0, workingSet.Value);
        Assert.Equal(1, activation.Tags.Length);
        Assert.Equal("grain_type", activation.Tags[0].Key);
        Assert.Same(metrics.GrainTypeTagValue, Assert.IsType<string>(activation.Tags[0].Value));
        // Measurement<T> defensively copies the tag array; the cached string must still be reused.
        Assert.Equal(1, workingSet.Tags.Length);
        Assert.Equal("grain_type", workingSet.Tags[0].Key);
        Assert.Same(metrics.GrainTypeTagValue, workingSet.Tags[0].Value);
        Assert.Same(metrics.GrainTypeTagValue, metrics.ActivationCount.Tags[0].Value);
        Assert.Same(metrics.GrainTypeTagValue, metrics.WorkingSetCount.Tags[0].Value);
    }

    [TestSuite("BVT"), TestProvider("None")]
    [Theory, TestCategory("BVT"), TestCategory("Runtime")]
    [InlineData(true)]
    [InlineData(false)]
    public void GrainTypeMetrics_CountersAreIndependentSnapshots(bool activationCounter)
    {
        var metrics = new GrainTypeMetrics(GrainType.Create("snapshot-orders"));
        var initialActivation = metrics.ActivationCount;
        var initialWorkingSet = metrics.WorkingSetCount;
        foreach (var (add, expected) in new[] { (true, 1), (true, 2), (false, 1), (false, 0) })
        {
            if (add)
            {
                if (activationCounter) metrics.OnActivationAdded();
                else metrics.OnWorkingSetAdded();
            }
            else
            {
                if (activationCounter) metrics.OnActivationRemoved();
                else metrics.OnWorkingSetRemoved();
            }

            Assert.Equal(activationCounter ? expected : 0, metrics.ActivationCount.Value);
            Assert.Equal(activationCounter ? 0 : expected, metrics.WorkingSetCount.Value);
            Assert.Equal(0, initialActivation.Value);
            Assert.Equal(0, initialWorkingSet.Value);
            Assert.Same(initialActivation.Tags[0].Value, metrics.ActivationCount.Tags[0].Value);
            Assert.Same(initialWorkingSet.Tags[0].Value, metrics.WorkingSetCount.Tags[0].Value);
            Assert.Same(metrics.GrainTypeTagValue, metrics.ActivationCount.Tags[0].Value);
            Assert.Same(metrics.GrainTypeTagValue, metrics.WorkingSetCount.Tags[0].Value);
        }
    }

    [TestSuite("BVT"), TestProvider("None")]
    [Fact, TestCategory("BVT"), TestCategory("Runtime")]
    public void GrainTypeMetrics_JoinedParallelUpdatesRetainExactCounts()
    {
        var metrics = new GrainTypeMetrics(GrainType.Create("parallel-orders"));
        const int updates = 4096;
        Parallel.For(0, updates, _ =>
        {
            metrics.OnActivationAdded();
            metrics.OnWorkingSetAdded();
            metrics.OnWorkingSetAdded();
        });
        var activation = metrics.ActivationCount;
        var workingSet = metrics.WorkingSetCount;
        Assert.Equal(updates, activation.Value);
        Assert.Equal(2 * updates, workingSet.Value);

        Parallel.For(0, updates, _ =>
        {
            metrics.OnActivationRemoved();
            metrics.OnWorkingSetRemoved();
            metrics.OnWorkingSetRemoved();
        });

        Assert.Equal(0, metrics.ActivationCount.Value);
        Assert.Equal(0, metrics.WorkingSetCount.Value);
        Assert.Equal(updates, activation.Value);
        Assert.Equal(2 * updates, workingSet.Value);
        Assert.Same(activation.Tags[0].Value, metrics.WorkingSetCount.Tags[0].Value);
        Assert.Same(workingSet.Tags[0].Value, metrics.ActivationCount.Tags[0].Value);
    }

    [TestSuite("BVT"), TestProvider("None")]
    [Fact, TestCategory("BVT"), TestCategory("Runtime")]
    public void Cache_ReusesEntriesWithoutRegisteringMisses()
    {
        using var fixture = new CatalogMetricFixture();
        using var other = new CatalogMetricFixture();
        var instruments = fixture.Instruments;
        instruments.RegisterActivationCountObserve();
        instruments.RegisterActivationWorkingSetObserve();
        var observations = new List<RecordedMeasurement<int>>();
        using var listener = fixture.Capture(observations);
        var type = GrainType.Create("cached-orders");

        Assert.False(instruments.TryGetGrainTypeMetrics(type, out var missing));
        Assert.Null(missing);
        listener.RecordObservableInstruments();
        Assert.Empty(observations);
        Assert.False(instruments.TryGetGrainTypeMetrics(type, out missing));
        Assert.Null(missing);

        var entry = instruments.GetGrainTypeMetrics(type);
        Assert.True(instruments.TryGetGrainTypeMetrics(GrainType.Create("cached-orders"), out var found));
        Assert.Same(entry, found);
        Assert.Same(entry, instruments.GetGrainTypeMetrics(GrainType.Create("cached-orders")));
        Assert.Same(entry.GrainTypeTagValue, found.GrainTypeTagValue);
        listener.RecordObservableInstruments();
        Assert.Equal(2, observations.Count);
        foreach (var instrumentName in new[] { InstrumentNames.CATALOG_ACTIVATION_COUNT, InstrumentNames.CATALOG_ACTIVATION_WORKING_SET })
        {
            var observation = Assert.Single(observations, item => item.Instrument.Name == instrumentName);
            Assert.IsType<ObservableGauge<int>>(observation.Instrument);
            Assert.Equal(0, observation.Value);
            AssertTags(observation.Tags, ("grain_type", entry.GrainTypeTagValue));
        }

        var unknown = instruments.GetGrainTypeMetrics(default);
        Assert.Equal("unknown", unknown.GrainTypeTagValue);
        Assert.Same(unknown, instruments.GetGrainTypeMetrics(default));
        Assert.True(instruments.TryGetGrainTypeMetrics(default, out var foundUnknown));
        Assert.Same(unknown, foundUnknown);
        var different = instruments.GetGrainTypeMetrics(GrainType.Create("cached-invoices"));
        Assert.NotSame(entry, different);
        Assert.NotEqual(entry.GrainTypeTagValue, different.GrainTypeTagValue);
        Assert.NotSame(unknown, instruments.GetGrainTypeMetrics(GrainType.Create("unknown")));
        var otherEntry = other.Instruments.GetGrainTypeMetrics(type);
        Assert.NotSame(entry, otherEntry);
        entry.OnActivationAdded();
        entry.OnWorkingSetAdded();
        Assert.Equal(1, entry.ActivationCount.Value);
        Assert.Equal(1, entry.WorkingSetCount.Value);
        Assert.Equal(0, otherEntry.ActivationCount.Value);
        Assert.Equal(0, otherEntry.WorkingSetCount.Value);
        Assert.Equal(0, different.ActivationCount.Value);
        Assert.Equal(0, different.WorkingSetCount.Value);
    }

    [TestSuite("BVT"), TestProvider("None")]
    [Fact, TestCategory("BVT"), TestCategory("Runtime")]
    public async Task Cache_ConcurrentFirstRegistrationSharesOneReference()
    {
        using var fixture = new CatalogMetricFixture();
        const int workers = 8;
        using var barrier = new Barrier(workers);
        var cancellationToken = TestContext.Current.CancellationToken;
        var type = GrainType.Create("concurrent-first-registration");
        Assert.False(fixture.Instruments.TryGetGrainTypeMetrics(type, out _));
        var tasks = new Task<GrainTypeMetrics>[workers];
        for (var i = 0; i < workers; i++)
        {
            // Dedicated workers prevent thread-pool starvation at the first-use barrier.
            tasks[i] = Task.Factory.StartNew(() =>
            {
                barrier.SignalAndWait(cancellationToken);
                var result = fixture.Instruments.GetGrainTypeMetrics(type);
                result.OnActivationAdded();
                result.OnWorkingSetAdded();
                return result;
            }, cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        var results = await Task.WhenAll(tasks);
        var first = results[0];
        foreach (var result in results)
        {
            Assert.Same(first, result);
            Assert.Same(first.GrainTypeTagValue, result.GrainTypeTagValue);
        }
        Assert.Equal(workers, first.ActivationCount.Value);
        Assert.Equal(workers, first.WorkingSetCount.Value);
        Parallel.ForEach(results, result =>
        {
            result.OnActivationRemoved();
            result.OnWorkingSetRemoved();
        });
        Assert.Equal(0, first.ActivationCount.Value);
        Assert.Equal(0, first.WorkingSetCount.Value);
        Assert.Same(first, fixture.Instruments.GetGrainTypeMetrics(type));
        Assert.True(fixture.Instruments.TryGetGrainTypeMetrics(type, out var retained));
        Assert.Same(first, retained);
        Assert.Same(first.GrainTypeTagValue, retained.GrainTypeTagValue);
    }

    [TestSuite("BVT"), TestProvider("None")]
    [Fact, TestCategory("BVT"), TestCategory("Runtime")]
    public void LifecycleHistograms_PreserveValuesDimensionsAndTypeReferences()
    {
        using var fixture = new CatalogMetricFixture();
        var first = fixture.Instruments.GetGrainTypeMetrics(GrainType.Create("histogram-orders")).GrainTypeTagValue;
        var second = fixture.Instruments.GetGrainTypeMetrics(GrainType.Create("histogram-invoices")).GrainTypeTagValue;
        var observations = new List<RecordedMeasurement<double>>();
        using var listener = fixture.Capture(observations);

        fixture.Instruments.OnActivationCompleted(TimeSpan.FromTicks(123450), "success", true, first);
        fixture.Instruments.OnActivationCompleted(TimeSpan.FromTicks(-25000), "directory_error", false, second);
        fixture.Instruments.OnDeactivationCompleted(TimeSpan.FromTicks(346250), "collection", first);
        fixture.Instruments.OnDeactivationCompleted(TimeSpan.FromTicks(-25000), "migration", second);

        Assert.Collection(observations,
            item => AssertHistogram(item, InstrumentNames.CATALOG_ACTIVATION_LATENCY, 12.345, ("status", "success"), ("directory", "enabled"), ("grain_type", first)),
            item => AssertHistogram(item, InstrumentNames.CATALOG_ACTIVATION_LATENCY, 0, ("status", "directory_error"), ("directory", "disabled"), ("grain_type", second)),
            item => AssertHistogram(item, InstrumentNames.CATALOG_DEACTIVATION_LATENCY, 34.625, ("via", "collection"), ("grain_type", first)),
            item => AssertHistogram(item, InstrumentNames.CATALOG_DEACTIVATION_LATENCY, -2.5, ("via", "migration"), ("grain_type", second)));
    }

    [TestSuite("BVT"), TestProvider("None")]
    [Theory, TestCategory("BVT"), TestCategory("Runtime")]
    [InlineData("none", true, "error")]
    [InlineData("none", false, "error")]
    [InlineData("success", true, "success")]
    [InlineData("success", false, "success")]
    [InlineData("failed", true, "error")]
    [InlineData("failed", false, "error")]
    [InlineData("failed-canceled", true, "canceled")]
    [InlineData("failed-canceled", false, "canceled")]
    [InlineData("canceled", true, "canceled")]
    [InlineData("canceled", false, "canceled")]
    [InlineData("duplicate", true, "duplicate")]
    [InlineData("duplicate", false, "duplicate")]
    [InlineData("duplicate-canceled", true, "duplicate")]
    [InlineData("duplicate-canceled", false, "duplicate")]
    [InlineData("directory-error", true, "directory_error")]
    [InlineData("directory-error", false, "directory_error")]
    [InlineData("directory-canceled", true, "canceled")]
    [InlineData("directory-canceled", false, "canceled")]
    public void ActivationTracker_TerminalOutcomesRecordOnce(string outcome, bool usesDirectory, string expectedStatus)
    {
        using var fixture = new CatalogMetricFixture();
        var type = fixture.Instruments.GetGrainTypeMetrics(GrainType.Create("tracker-orders")).GrainTypeTagValue;
        var observations = new List<RecordedMeasurement<double>>();
        using var listener = fixture.Capture(observations);
        var tracker = CatalogInstruments.ActivationMetricTracker.Start(fixture.Instruments, usesDirectory, type);
        SetActivationOutcome(ref tracker, outcome, new InvalidOperationException("directory unavailable"));
        tracker.Record();
        tracker.Record();
        tracker.Succeeded();
        tracker.Record();
        tracker.Failed(true);
        tracker.Record();

        var observation = Assert.Single(observations);
        Assert.IsType<Histogram<double>>(observation.Instrument);
        Assert.Equal(InstrumentNames.CATALOG_ACTIVATION_LATENCY, observation.Instrument.Name);
        Assert.Equal("ms", observation.Instrument.Unit);
        Assert.True(double.IsFinite(observation.Value) && observation.Value >= 0);
        AssertTags(observation.Tags, ("status", expectedStatus), ("directory", usesDirectory ? "enabled" : "disabled"), ("grain_type", type));
    }

    [TestSuite("BVT"), TestProvider("None")]
    [Theory, TestCategory("BVT"), TestCategory("Runtime")]
    [InlineData("unknown", false)]
    [InlineData("collection", false)]
    [InlineData("deactivateOnIdle", false)]
    [InlineData("deactivateStuckActivation", false)]
    [InlineData("migration", false)]
    [InlineData("unknown", true)]
    [InlineData("collection", true)]
    [InlineData("deactivateOnIdle", true)]
    [InlineData("deactivateStuckActivation", true)]
    [InlineData("migration", true)]
    public void DeactivationTracker_AllReasonsRecordOnceOnSameLocal(string via, bool recordIfNeededFirst)
    {
        using var fixture = new CatalogMetricFixture();
        var type = fixture.Instruments.GetGrainTypeMetrics(GrainType.Create("deactivation-orders")).GrainTypeTagValue;
        var observations = new List<RecordedMeasurement<double>>();
        using var listener = fixture.Capture(observations);
        var tracker = WithDeactivationReason(CatalogInstruments.DeactivationMetricTracker.Start(fixture.Instruments, type), via);
        if (recordIfNeededFirst) tracker.RecordIfNeeded();
        else tracker.Record(); // Intentionally ignore the returned struct: this local must still be marked recorded.
        tracker.Record();
        tracker.RecordIfNeeded();
        tracker = tracker.Migration();
        tracker.RecordIfNeeded();
        tracker = tracker.Record();
        tracker.Record();

        var observation = Assert.Single(observations);
        Assert.IsType<Histogram<double>>(observation.Instrument);
        Assert.Equal(InstrumentNames.CATALOG_DEACTIVATION_LATENCY, observation.Instrument.Name);
        Assert.Equal("ms", observation.Instrument.Unit);
        Assert.True(double.IsFinite(observation.Value) && observation.Value >= 0);
        AssertTags(observation.Tags, ("via", via), ("grain_type", type));
    }

    [TestSuite("BVT"), TestProvider("None")]
    [Fact, TestCategory("BVT"), TestCategory("Runtime")]
    public void Trackers_ReentrantCallbacksCannotRecordTheSameLocalTwice()
    {
        using var fixture = new CatalogMetricFixture();
        var type = fixture.Instruments.GetGrainTypeMetrics(GrainType.Create("reentrant-orders")).GrainTypeTagValue;
        var observations = new List<RecordedMeasurement<double>>();
        CatalogInstruments.ActivationMetricTracker activation = default;
        CatalogInstruments.DeactivationMetricTracker deactivation = default;
        using var listener = fixture.Listen<double>((instrument, value, tags, _) =>
        {
            observations.Add(new(instrument, value, tags.ToArray()));
            // Bound recursion so a regression fails by event count, not by stack overflow.
            if (instrument.Name == InstrumentNames.CATALOG_ACTIVATION_LATENCY && observations.Count == 1)
            {
                activation.Canceled();
                activation.Record();
            }
            else if (instrument.Name == InstrumentNames.CATALOG_DEACTIVATION_LATENCY && observations.Count == 2)
            {
                deactivation = deactivation.Migration();
                deactivation.RecordIfNeeded();
            }
        });
        activation = CatalogInstruments.ActivationMetricTracker.Start(fixture.Instruments, true, type);
        activation.Succeeded();
        activation.Record();
        deactivation = CatalogInstruments.DeactivationMetricTracker.Start(fixture.Instruments, type).Collection();
        deactivation.Record();

        Assert.Collection(observations,
            item => AssertTrackerMeasurement(item, InstrumentNames.CATALOG_ACTIVATION_LATENCY, ("status", "success"), ("directory", "enabled"), ("grain_type", type)),
            item => AssertTrackerMeasurement(item, InstrumentNames.CATALOG_DEACTIVATION_LATENCY, ("via", "collection"), ("grain_type", type)));
    }

    [TestSuite("BVT"), TestProvider("None")]
    [Theory, TestCategory("BVT"), TestCategory("Runtime")]
    [InlineData("created", InstrumentNames.CATALOG_ACTIVATION_CREATED, null)]
    [InlineData("destroyed", InstrumentNames.CATALOG_ACTIVATION_DESTROYED, null)]
    [InlineData("failed", InstrumentNames.CATALOG_ACTIVATION_FAILED_TO_ACTIVATE, null)]
    [InlineData("concurrent", InstrumentNames.CATALOG_ACTIVATION_CONCURRENT_REGISTRATION_ATTEMPTS, null)]
    [InlineData("nonexistent", InstrumentNames.CATALOG_ACTIVATION_NON_EXISTENT_ACTIVATIONS, null)]
    [InlineData("collection", InstrumentNames.CATALOG_ACTIVATION_SHUTDOWN, "collection")]
    [InlineData("deactivateOnIdle", InstrumentNames.CATALOG_ACTIVATION_SHUTDOWN, "deactivateOnIdle")]
    [InlineData("deactivateStuckActivation", InstrumentNames.CATALOG_ACTIVATION_SHUTDOWN, "deactivateStuckActivation")]
    [InlineData("migration", InstrumentNames.CATALOG_ACTIVATION_SHUTDOWN, "migration")]
    [InlineData("pass", InstrumentNames.CATALOG_ACTIVATION_COLLECTION_NUMBER_OF_COLLECTIONS, null)]
    public void CatalogEvents_EmitExactPerTypeDeltasAndTagContracts(string operation, string instrumentName, string? via)
    {
        using var fixture = new CatalogMetricFixture();
        var first = fixture.Instruments.GetGrainTypeMetrics(GrainType.Create("event-orders")).GrainTypeTagValue;
        var second = fixture.Instruments.GetGrainTypeMetrics(GrainType.Create("event-invoices")).GrainTypeTagValue;
        var unknown = fixture.Instruments.GetGrainTypeMetrics(default).GrainTypeTagValue;
        var untouched = fixture.Instruments.GetGrainTypeMetrics(GrainType.Create("event-untouched"));
        var observations = new List<RecordedMeasurement<int>>();
        using var listener = fixture.Capture(observations);
        var names = new[] { first, second, first, unknown };
        foreach (var name in names) RecordCatalogEvent(fixture.Instruments, operation, name);

        Assert.Equal(names.Length, observations.Count);
        for (var i = 0; i < observations.Count; i++)
        {
            var item = observations[i];
            Assert.IsType<Counter<int>>(item.Instrument);
            Assert.Equal(instrumentName, item.Instrument.Name);
            Assert.Equal(1, item.Value);
            if (operation == "pass") Assert.Empty(item.Tags);
            else if (via is null) AssertTags(item.Tags, ("grain_type", names[i]));
            else AssertTags(item.Tags, ("via", via), ("grain_type", names[i]));
        }
        if (operation != "pass")
        {
            Assert.Equal(2, observations.Where(item => item.Tags.Any(tag => Equals(tag.Value, first))).Sum(item => item.Value));
            Assert.Equal(1, observations.Where(item => item.Tags.Any(tag => Equals(tag.Value, second))).Sum(item => item.Value));
            Assert.Equal(1, observations.Where(item => item.Tags.Any(tag => Equals(tag.Value, unknown))).Sum(item => item.Value));
        }
        Assert.DoesNotContain(observations, item => item.Tags.Any(tag => Equals(tag.Value, untouched.GrainTypeTagValue)));
        Assert.Equal(0, untouched.ActivationCount.Value);
        Assert.Equal(0, untouched.WorkingSetCount.Value);
    }

    [TestSuite("BVT"), TestProvider("None")]
    [Fact, TestCategory("BVT"), TestCategory("Runtime")]
    public void Trackers_DefaultAndDisabledNeverResurrect()
    {
        using var fixture = new CatalogMetricFixture();
        Assert.False(fixture.Instruments.ActivationLatencyEnabled);
        Assert.False(fixture.Instruments.DeactivationLatencyEnabled);
        Assert.False(fixture.Instruments.NonExistentActivationsEnabled);
        CatalogInstruments.ActivationMetricTracker defaultActivation = default;
        CatalogInstruments.DeactivationMetricTracker defaultDeactivation = default;
        var disabledActivation = CatalogInstruments.ActivationMetricTracker.Start(fixture.Instruments, true, "disabled-orders");
        var disabledDeactivation = CatalogInstruments.DeactivationMetricTracker.Start(fixture.Instruments, "disabled-orders");
        var exception = new InvalidOperationException("directory unavailable");
        ExerciseInactiveTrackers(ref defaultActivation, ref defaultDeactivation, exception);
        ExerciseInactiveTrackers(ref disabledActivation, ref disabledDeactivation, exception);

        var observations = new List<RecordedMeasurement<double>>();
        using var listener = fixture.Capture(observations);
        Assert.True(fixture.Instruments.ActivationLatencyEnabled);
        Assert.True(fixture.Instruments.DeactivationLatencyEnabled);
        ExerciseInactiveTrackers(ref defaultActivation, ref defaultDeactivation, exception);
        ExerciseInactiveTrackers(ref disabledActivation, ref disabledDeactivation, exception);
        Assert.Empty(observations);

        var activation = CatalogInstruments.ActivationMetricTracker.Start(fixture.Instruments, false, "enabled-orders");
        activation.Succeeded();
        activation.Record();
        var deactivation = CatalogInstruments.DeactivationMetricTracker.Start(fixture.Instruments, "enabled-orders").Migration();
        deactivation.RecordIfNeeded();
        Assert.Collection(observations,
            item => AssertTrackerMeasurement(item, InstrumentNames.CATALOG_ACTIVATION_LATENCY, ("status", "success"), ("directory", "disabled"), ("grain_type", "enabled-orders")),
            item => AssertTrackerMeasurement(item, InstrumentNames.CATALOG_DEACTIVATION_LATENCY, ("via", "migration"), ("grain_type", "enabled-orders")));
    }

    [TestSuite("BVT"), TestProvider("None")]
    [Fact, TestCategory("BVT"), TestCategory("Runtime")]
    public void WarmCacheHits_AllocateZero()
    {
        using var fixture = new CatalogMetricFixture();
        var type = GrainType.Create("warm-cache-orders");
        var entry = fixture.Instruments.GetGrainTypeMetrics(type);
        var unknown = fixture.Instruments.GetGrainTypeMetrics(default);
        entry.OnActivationAdded();
        entry.OnWorkingSetAdded();
        var mismatches = 0;
        void Lookup()
        {
            var hit = fixture.Instruments.GetGrainTypeMetrics(type);
            if (!fixture.Instruments.TryGetGrainTypeMetrics(type, out var found)
                || !ReferenceEquals(entry, hit) || !ReferenceEquals(entry, found)
                || !ReferenceEquals(entry.GrainTypeTagValue, hit.GrainTypeTagValue)
                || !ReferenceEquals(entry.GrainTypeTagValue, found.GrainTypeTagValue)
                || !ReferenceEquals(unknown, fixture.Instruments.GetGrainTypeMetrics(default)))
            {
                mismatches++;
            }
        }
        for (var i = 0; i < 1024; i++) Lookup();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 4096; i++) Lookup();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0L, allocated);
        Assert.Equal(0, mismatches);
        Assert.Equal(1, entry.ActivationCount.Value);
        Assert.Equal(1, entry.WorkingSetCount.Value);
        Assert.Equal(0, unknown.ActivationCount.Value);
    }

    [TestSuite("BVT"), TestProvider("None")]
    [Fact, TestCategory("BVT"), TestCategory("Runtime")]
    public void WarmCatalogRecording_AllocatesZeroWithLiveCallbacks()
    {
        using var fixture = new CatalogMetricFixture();
        var type = fixture.Instruments.GetGrainTypeMetrics(GrainType.Create("warm-recording-orders")).GrainTypeTagValue;
        var exception = new InvalidOperationException("directory unavailable");
        var counts = new int[9];
        var sums = new double[9];
        var tagMismatches = 0;
        var invalidValues = 0;
        void Observe(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var index = CatalogInstrumentIndex(instrument.Name);
            if (index < 0) { invalidValues++; return; }
            counts[index]++;
            sums[index] += value;
            if (!double.IsFinite(value) || value < 0 || (index < 7 && value != 1)) invalidValues++;
            var expectedTagCount = index == 6 ? 0 : index == 7 ? 3 : index is 5 or 8 ? 2 : 1;
            if (tags.Length != expectedTagCount) tagMismatches++;
            var typeTags = 0;
            foreach (var tag in tags)
            {
                if (tag.Key == "grain_type")
                {
                    typeTags++;
                    if (!ReferenceEquals(type, tag.Value)) tagMismatches++;
                }
                else if (tag.Key is not ("status" or "directory" or "via")) tagMismatches++;
            }
            if (typeTags != (index == 6 ? 0 : 1)) tagMismatches++;
        }
        using var integerListener = fixture.Listen<int>((instrument, value, tags, _) => Observe(instrument, value, tags));
        using var doubleListener = fixture.Listen<double>((instrument, value, tags, _) => Observe(instrument, value, tags));
        Assert.True(fixture.Instruments.ActivationLatencyEnabled);
        Assert.True(fixture.Instruments.DeactivationLatencyEnabled);
        Assert.True(fixture.Instruments.NonExistentActivationsEnabled);
        for (var i = 0; i < 1024; i++) RecordCatalogBatch(fixture.Instruments, type, exception);
        Array.Clear(counts);
        Array.Clear(sums);
        tagMismatches = invalidValues = 0;
        const int iterations = 2048;

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < iterations; i++) RecordCatalogBatch(fixture.Instruments, type, exception);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0L, allocated);
        Assert.Equal(new[] { iterations, iterations, iterations, iterations, iterations, 4 * iterations, iterations, 20 * iterations, 6 * iterations }, counts);
        for (var i = 0; i < 7; i++) Assert.Equal((double)counts[i], sums[i]);
        Assert.True(sums[7] >= 12.5 * iterations);
        Assert.True(sums[8] >= 34.25 * iterations);
        Assert.Equal(0, invalidValues);
        Assert.Equal(0, tagMismatches);
    }

    [TestSuite("BVT"), TestProvider("None")]
    [Fact, TestCategory("BVT"), TestCategory("Runtime")]
    public void DisabledRecording_AllocatesZero()
    {
        using var fixture = new CatalogMetricFixture();
        var type = fixture.Instruments.GetGrainTypeMetrics(GrainType.Create("disabled-recording-orders")).GrainTypeTagValue;
        var exception = new InvalidOperationException("directory unavailable");
        Assert.False(fixture.Instruments.ActivationLatencyEnabled);
        Assert.False(fixture.Instruments.DeactivationLatencyEnabled);
        Assert.False(fixture.Instruments.NonExistentActivationsEnabled);
        for (var i = 0; i < 1024; i++) RecordCatalogBatch(fixture.Instruments, type, exception);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 2048; i++) RecordCatalogBatch(fixture.Instruments, type, exception);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0L, allocated);

        var counts = new int[9];
        using var integers = fixture.Listen<int>((instrument, _, _, _) => counts[CatalogInstrumentIndex(instrument.Name)]++);
        using var doubles = fixture.Listen<double>((instrument, _, _, _) => counts[CatalogInstrumentIndex(instrument.Name)]++);
        Assert.All(counts, count => Assert.Equal(0, count));
        RecordCatalogBatch(fixture.Instruments, type, exception);
        Assert.Equal(new[] { 1, 1, 1, 1, 1, 4, 1, 20, 6 }, counts);
    }

    private static void SetActivationOutcome(ref CatalogInstruments.ActivationMetricTracker tracker, string outcome, Exception exception)
    {
        switch (outcome)
        {
            case "none": break;
            case "success": tracker.Succeeded(); break;
            case "failed": tracker.Failed(false); break;
            case "failed-canceled": tracker.Failed(true); break;
            case "canceled": tracker.Canceled(); break;
            case "duplicate": tracker.DirectoryRegistrationFailed(null, false); break;
            case "duplicate-canceled": tracker.DirectoryRegistrationFailed(null, true); break;
            case "directory-error": tracker.DirectoryRegistrationFailed(exception, false); break;
            case "directory-canceled": tracker.DirectoryRegistrationFailed(exception, true); break;
            default: throw new ArgumentOutOfRangeException(nameof(outcome));
        }
    }

    private static CatalogInstruments.DeactivationMetricTracker WithDeactivationReason(CatalogInstruments.DeactivationMetricTracker tracker, string via) => via switch
    {
        "unknown" => tracker,
        "collection" => tracker.Collection(),
        "deactivateOnIdle" => tracker.DeactivateOnIdle(),
        "deactivateStuckActivation" => tracker.DeactivateStuckActivation(),
        "migration" => tracker.Migration(),
        _ => throw new ArgumentOutOfRangeException(nameof(via))
    };

    private static void RecordCatalogEvent(CatalogInstruments instruments, string operation, string type)
    {
        switch (operation)
        {
            case "created": instruments.OnActivationCreated(type); break;
            case "destroyed": instruments.OnActivationDestroyed(type); break;
            case "failed": instruments.OnActivationFailedToActivate(type); break;
            case "concurrent": instruments.OnActivationConcurrentRegistrationAttempt(type); break;
            case "nonexistent": instruments.OnNonExistentActivation(type); break;
            case "collection": instruments.ActivationShutdownViaCollection(type); break;
            case "deactivateOnIdle": instruments.ActivationShutdownViaDeactivateOnIdle(type); break;
            case "deactivateStuckActivation": instruments.ActivationShutdownViaDeactivateStuckActivation(type); break;
            case "migration": instruments.ActivationShutdownViaMigration(type); break;
            case "pass": instruments.OnActivationCollected(); break;
            default: throw new ArgumentOutOfRangeException(nameof(operation));
        }
    }

    private static readonly string[] ActivationOutcomes = ["none", "success", "failed", "failed-canceled", "canceled", "duplicate", "duplicate-canceled", "directory-error", "directory-canceled"];
    private static readonly string[] DeactivationReasons = ["unknown", "collection", "deactivateOnIdle", "deactivateStuckActivation", "migration"];
    private static readonly string[] CatalogEvents = ["created", "destroyed", "failed", "concurrent", "nonexistent", "collection", "deactivateOnIdle", "deactivateStuckActivation", "migration", "pass"];

    private static void ExerciseInactiveTrackers(ref CatalogInstruments.ActivationMetricTracker activation, ref CatalogInstruments.DeactivationMetricTracker deactivation, Exception exception)
    {
        foreach (var outcome in ActivationOutcomes)
        {
            SetActivationOutcome(ref activation, outcome, exception);
            activation.Record();
        }
        foreach (var via in DeactivationReasons)
        {
            deactivation = WithDeactivationReason(deactivation, via);
            deactivation.Record();
            deactivation.RecordIfNeeded();
        }
    }

    private static void RecordCatalogBatch(CatalogInstruments instruments, string type, Exception exception)
    {
        foreach (var operation in CatalogEvents) RecordCatalogEvent(instruments, operation, type);
        instruments.OnActivationCompleted(TimeSpan.FromTicks(125000), "success", true, type);
        instruments.OnActivationCompleted(TimeSpan.FromTicks(-125000), "error", false, type);
        instruments.OnDeactivationCompleted(TimeSpan.FromTicks(342500), "collection", type);
        foreach (var outcome in ActivationOutcomes)
        {
            for (var directory = 0; directory < 2; directory++)
            {
                var activation = CatalogInstruments.ActivationMetricTracker.Start(instruments, directory != 0, type);
                SetActivationOutcome(ref activation, outcome, exception);
                activation.Record();
                activation.Record();
            }
        }
        foreach (var via in DeactivationReasons)
        {
            var deactivation = WithDeactivationReason(CatalogInstruments.DeactivationMetricTracker.Start(instruments, type), via);
            deactivation.RecordIfNeeded();
            deactivation.Record();
        }
    }

    private static int CatalogInstrumentIndex(string name) => name switch
    {
        InstrumentNames.CATALOG_ACTIVATION_CREATED => 0,
        InstrumentNames.CATALOG_ACTIVATION_DESTROYED => 1,
        InstrumentNames.CATALOG_ACTIVATION_FAILED_TO_ACTIVATE => 2,
        InstrumentNames.CATALOG_ACTIVATION_CONCURRENT_REGISTRATION_ATTEMPTS => 3,
        InstrumentNames.CATALOG_ACTIVATION_NON_EXISTENT_ACTIVATIONS => 4,
        InstrumentNames.CATALOG_ACTIVATION_SHUTDOWN => 5,
        InstrumentNames.CATALOG_ACTIVATION_COLLECTION_NUMBER_OF_COLLECTIONS => 6,
        InstrumentNames.CATALOG_ACTIVATION_LATENCY => 7,
        InstrumentNames.CATALOG_DEACTIVATION_LATENCY => 8,
        _ => -1
    };

    private static void AssertHistogram(RecordedMeasurement<double> item, string instrumentName, double value, params (string Key, string Value)[] tags)
    {
        Assert.IsType<Histogram<double>>(item.Instrument);
        Assert.Equal(instrumentName, item.Instrument.Name);
        Assert.Equal("ms", item.Instrument.Unit);
        Assert.Equal(value, item.Value);
        AssertTags(item.Tags, tags);
    }

    private static void AssertTrackerMeasurement(RecordedMeasurement<double> item, string instrumentName, params (string Key, string Value)[] tags)
    {
        Assert.IsType<Histogram<double>>(item.Instrument);
        Assert.Equal(instrumentName, item.Instrument.Name);
        Assert.Equal("ms", item.Instrument.Unit);
        Assert.True(double.IsFinite(item.Value) && item.Value >= 0);
        AssertTags(item.Tags, tags);
    }

    private static void AssertTags(KeyValuePair<string, object?>[] actual, params (string Key, string Value)[] expected)
    {
        Assert.Equal(expected.Length, actual.Length);
        foreach (var (key, value) in expected)
        {
            var tag = Assert.Single(actual, tag => tag.Key == key);
            Assert.Equal(value, Assert.IsType<string>(tag.Value));
            if (key == "grain_type") Assert.Same(value, tag.Value);
        }
    }

    private sealed record RecordedMeasurement<T>(Instrument Instrument, T Value, KeyValuePair<string, object?>[] Tags);

    private sealed class CatalogMetricFixture : IDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly Meter _meter;
        public CatalogInstruments Instruments { get; }

        public CatalogMetricFixture()
        {
            _provider = new ServiceCollection().AddMetrics().BuildServiceProvider();
            var orleans = new OrleansInstruments(_provider.GetRequiredService<IMeterFactory>());
            _meter = orleans.Meter;
            Instruments = new CatalogInstruments(orleans);
        }

        public MeterListener Listen<T>(MeasurementCallback<T> callback) where T : struct
        {
            var listener = new MeterListener();
            listener.InstrumentPublished = (instrument, owner) =>
            {
                if (ReferenceEquals(instrument.Meter, _meter) && instrument is Instrument<T> or ObservableInstrument<T>)
                {
                    owner.EnableMeasurementEvents(instrument);
                }
            };
            listener.SetMeasurementEventCallback(callback);
            listener.Start();
            return listener;
        }

        public MeterListener Capture<T>(List<RecordedMeasurement<T>> observations) where T : struct =>
            Listen<T>((instrument, value, tags, _) => observations.Add(new(instrument, value, tags.ToArray())));

        public void Dispose() => _provider.Dispose();
    }

    [TestSuite("BVT"), TestProvider("None")]
    [Fact, TestCategory("BVT"), TestCategory("Runtime")]
    public void Directory_ExactMembershipAndLateSnapshotGauges()
    {
        using var fixture = new CatalogMetricFixture();
        using var directory = new ActivationDirectory(fixture.Instruments);
        var first = DirectoryContext("directory-orders", "one");
        var second = DirectoryContext("directory-orders", "two");
        var other = DirectoryContext("directory-invoices", "one");
        var loser = DirectoryContext("directory-orders", "one");
        directory.RecordNewTarget(first);
        directory.RecordNewTarget(second);
        directory.RecordNewTarget(other);
        directory.RecordNewTarget(first);
        directory.RecordNewTarget(loser);

        var observations = new List<RecordedMeasurement<int>>();
        using var listener = fixture.Capture(observations); // Deliberately attach after registration.
        Assert.Same(first, directory.FindTarget(first.GrainId));
        Assert.Same(second, directory.FindTarget(second.GrainId));
        Assert.Same(other, directory.FindTarget(other.GrainId));
        Assert.Null(directory.FindTarget(GrainId.Create("never-registered", "missing")));
        AssertDirectorySnapshot(fixture, directory, listener, observations, ("directory-orders", 2), ("directory-invoices", 1));
        AssertDirectorySnapshot(fixture, directory, listener, observations, ("directory-orders", 2), ("directory-invoices", 1));

        Assert.False(directory.RemoveTarget(loser));
        Assert.Same(first, directory.FindTarget(first.GrainId));
        AssertDirectorySnapshot(fixture, directory, listener, observations, ("directory-orders", 2), ("directory-invoices", 1));
        Assert.True(directory.RemoveTarget(first));
        Assert.False(directory.RemoveTarget(first));
        Assert.Null(directory.FindTarget(first.GrainId));
        Assert.Same(other, directory.FindTarget(other.GrainId));
        AssertDirectorySnapshot(fixture, directory, listener, observations, ("directory-orders", 1), ("directory-invoices", 1));
        Assert.True(directory.RemoveTarget(second));
        Assert.True(directory.RemoveTarget(other));
        Assert.Empty(directory);
        AssertDirectorySnapshot(fixture, directory, listener, observations, ("directory-orders", 0), ("directory-invoices", 0));
        AssertDirectorySnapshot(fixture, directory, listener, observations, ("directory-orders", 0), ("directory-invoices", 0));
    }

    [TestSuite("BVT"), TestProvider("None")]
    [Fact, TestCategory("BVT"), TestCategory("Runtime")]
    public async Task Directory_ConcurrentRegistrationAndRemovalReconcileAfterJoin()
    {
        using var fixture = new CatalogMetricFixture();
        using var directory = new ActivationDirectory(fixture.Instruments);
        var pairs = Enumerable.Range(0, 24).Select(i => (
            First: DirectoryContext($"directory-race-{i % 3}", i.ToString()),
            Second: DirectoryContext($"directory-race-{i % 3}", i.ToString()))).ToArray();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var additions = pairs.SelectMany(pair => new[] { pair.First, pair.Second }).Select(async context =>
        {
            await start.Task;
            directory.RecordNewTarget(context);
            directory.RecordNewTarget(context);
            return fixture.Instruments.GetGrainTypeMetrics(context.GrainId.Type);
        }).ToArray();
        start.SetResult();
        var metrics = await Task.WhenAll(additions).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var winners = pairs.Select(pair =>
        {
            var winner = directory.FindTarget(pair.First.GrainId);
            Assert.True(ReferenceEquals(pair.First, winner) || ReferenceEquals(pair.Second, winner));
            return winner!;
        }).ToArray();
        Assert.Equal(24, directory.Count);
        Assert.Equal(24, directory.Count());
        foreach (var entry in metrics)
        {
            Assert.Same(entry.GrainTypeTagValue, fixture.Instruments.GetGrainTypeMetrics(GrainType.Create(entry.GrainTypeTagValue)).GrainTypeTagValue);
        }

        var observations = new List<RecordedMeasurement<int>>();
        using var listener = fixture.Capture(observations);
        AssertDirectorySnapshot(fixture, directory, listener, observations, ("directory-race-0", 8), ("directory-race-1", 8), ("directory-race-2", 8));
        var losingRemovals = pairs.Select((pair, index) => Task.Run(() =>
            directory.RemoveTarget(ReferenceEquals(winners[index], pair.First) ? pair.Second : pair.First))).ToArray();
        Assert.All(await Task.WhenAll(losingRemovals), removed => Assert.False(removed));
        AssertDirectorySnapshot(fixture, directory, listener, observations, ("directory-race-0", 8), ("directory-race-1", 8), ("directory-race-2", 8));

        var removeStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var removals = winners.Select(winner => Enumerable.Range(0, 4).Select(async _ =>
        {
            await removeStart.Task;
            return directory.RemoveTarget(winner);
        }).ToArray()).ToArray();
        removeStart.SetResult();
        await Task.WhenAll(removals.SelectMany(tasks => tasks)).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        foreach (var attempts in removals) Assert.Single(await Task.WhenAll(attempts), removed => removed);
        Assert.All(winners, winner => Assert.Null(directory.FindTarget(winner.GrainId)));
        Assert.Empty(directory);
        AssertDirectorySnapshot(fixture, directory, listener, observations, ("directory-race-0", 0), ("directory-race-1", 0), ("directory-race-2", 0));
        AssertDirectorySnapshot(fixture, directory, listener, observations, ("directory-race-0", 0), ("directory-race-1", 0), ("directory-race-2", 0));
    }

    [TestSuite("BVT"), TestProvider("None")]
    [Theory, TestCategory("BVT"), TestCategory("Runtime")]
    [InlineData(false, "none")]
    [InlineData(false, "sync")]
    [InlineData(false, "sync-fault")]
    [InlineData(false, "async")]
    [InlineData(false, "async-fault")]
    [InlineData(false, "async-throw")]
    [InlineData(true, "none")]
    [InlineData(true, "sync")]
    [InlineData(true, "sync-fault")]
    [InlineData(true, "async")]
    [InlineData(true, "async-fault")]
    [InlineData(true, "async-throw")]
    public async Task Directory_DisposalAlwaysRemovesTargets(bool asynchronous, string targetKind)
    {
        using var fixture = new CatalogMetricFixture();
        var directory = new ActivationDirectory(fixture.Instruments);
        var target = targetKind switch
        {
            "sync" or "sync-fault" => NSubstitute.Substitute.For<IGrainContext, IDisposable>(),
            "async" or "async-fault" or "async-throw" => NSubstitute.Substitute.For<IGrainContext, IAsyncDisposable>(),
            _ => NSubstitute.Substitute.For<IGrainContext>()
        };
        ConfigureDirectoryContext(target, "directory-disposal", "one");
        var invocations = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fault = new InvalidOperationException("Expected target disposal fault.");
        if (target is IDisposable disposable)
        {
            NSubstitute.SubstituteExtensions.When(disposable, item => item.Dispose()).Do(_ =>
            {
                invocations++;
                if (targetKind == "sync-fault") throw fault;
            });
        }
        if (target is IAsyncDisposable asyncDisposable)
        {
            NSubstitute.SubstituteExtensions.Returns(asyncDisposable.DisposeAsync(), _ =>
            {
                invocations++;
                if (targetKind == "async-throw") throw fault;
                return FinishDisposal();
            });
        }

        directory.RecordNewTarget(target);
        var observations = new List<RecordedMeasurement<int>>();
        using var listener = fixture.Capture(observations);
        AssertDirectorySnapshot(fixture, directory, listener, observations, ("directory-disposal", 1));
        Task disposal = Task.CompletedTask;
        try
        {
            if (asynchronous) disposal = ((IAsyncDisposable)directory).DisposeAsync().AsTask();
            else ((IDisposable)directory).Dispose();

            Assert.Null(directory.FindTarget(target.GrainId));
            Assert.Empty(directory);
            AssertDirectorySnapshot(fixture, directory, listener, observations, ("directory-disposal", 0));
            var expectedCalls = targetKind == "none" || (!asynchronous && targetKind.StartsWith("async", StringComparison.Ordinal)) ? 0 : 1;
            Assert.Equal(expectedCalls, invocations);
            if (asynchronous && targetKind is "async" or "async-fault") Assert.False(disposal.IsCompleted);
            release.SetResult();
            await disposal.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            if (asynchronous) await ((IAsyncDisposable)directory).DisposeAsync();
            else ((IDisposable)directory).Dispose();
            Assert.Equal(expectedCalls, invocations);
            Assert.False(directory.RemoveTarget(target));
            Assert.Null(directory.FindTarget(target.GrainId));
            AssertDirectorySnapshot(fixture, directory, listener, observations, ("directory-disposal", 0));
        }
        finally
        {
            release.TrySetResult();
            await disposal.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
            await ((IAsyncDisposable)directory).DisposeAsync();
        }

        async ValueTask FinishDisposal()
        {
            await release.Task;
            if (targetKind == "async-fault") throw fault;
        }
    }

    private static IGrainContext DirectoryContext(string type, string key)
    {
        var context = NSubstitute.Substitute.For<IGrainContext>();
        ConfigureDirectoryContext(context, type, key);
        return context;
    }

    private static void ConfigureDirectoryContext(IGrainContext context, string type, string key)
    {
        NSubstitute.SubstituteExtensions.Returns(context.GrainId, GrainId.Create(type, key));
        NSubstitute.SubstituteExtensions.Returns(context.Equals(NSubstitute.Arg.Any<IGrainContext>()),
            call => ReferenceEquals(context, call.Arg<IGrainContext>()));
    }

    private static void AssertDirectorySnapshot(
        CatalogMetricFixture fixture,
        ActivationDirectory directory,
        MeterListener listener,
        List<RecordedMeasurement<int>> observations,
        params (string Type, int Count)[] expected)
    {
        observations.Clear(); // A scrape is a snapshot, never a cumulative event total.
        listener.RecordObservableInstruments();
        Assert.Equal(expected.Length, observations.Count);
        foreach (var (type, count) in expected)
        {
            Assert.True(fixture.Instruments.TryGetGrainTypeMetrics(GrainType.Create(type), out var metrics));
            var item = Assert.Single(observations, item => Equals(Assert.Single(item.Tags).Value, type));
            Assert.IsType<ObservableGauge<int>>(item.Instrument);
            Assert.Equal(InstrumentNames.CATALOG_ACTIVATION_COUNT, item.Instrument.Name);
            Assert.Equal(count, item.Value);
            AssertTags(item.Tags, ("grain_type", metrics.GrainTypeTagValue));
        }
        Assert.Equal(expected.Sum(item => item.Count), directory.Count);
        Assert.Equal(directory.Count, observations.Sum(item => item.Value));
    }
}
