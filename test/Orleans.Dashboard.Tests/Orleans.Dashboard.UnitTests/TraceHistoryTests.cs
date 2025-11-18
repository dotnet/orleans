using Orleans.Dashboard.Metrics.History;
using Orleans.Dashboard.Model;
using System;
using System.Linq;
using Xunit;

namespace UnitTests;

public class TraceHistoryTests
{
    private readonly DateTime _startTime = DateTime.UtcNow;
    private int _seconds;

    private void Add(ITraceHistory history, int count)
    {
        for (var i = 0; i < count; i++)
        {
            history.Add(_startTime.AddSeconds(_seconds), "SILO1", new[]
            {
                new SiloGrainTraceEntry 
                {
                    Grain = "GRAIN1",
                    Method = "METHOD1",
                    Count = 1,
                    ExceptionCount = 0,
                    ElapsedTime = 10
                }
            });

            history.Add(_startTime.AddSeconds(_seconds), "SILO2", new[]
            {
                new SiloGrainTraceEntry 
                {
                    Grain = "GRAIN1",
                    Method = "METHOD1",
                    Count = 100,
                    ExceptionCount = 10,
                    ElapsedTime = 200
                }
            });

            _seconds++;
        }
    }

    private ITraceHistory CreateHistory() => new TraceHistory(100);

    [Fact]
    public void TestTraceHistoryIsLimitedTo100()
    {
        var history = CreateHistory();

        // Seed with 100 values
        Add(history, 100);

        // check there are 100 values in the results
        var grainTraceDictionary = history.QueryAll();

        Assert.Equal(100, grainTraceDictionary.Keys.Count);

        // Add another 10 values
        Add(history, 10);

        var grainTraceDictionary2 = history.QueryAll();

        Assert.Equal(100, grainTraceDictionary2.Keys.Count);
    }

    [Fact]
    public void TestTraceHistoryQueryAll()
    {
        var history = CreateHistory();

        Add(history, 100);

        var silo1History = history.QuerySilo("SILO1");

        Assert.Equal(100, silo1History.Keys.Count);
    }

    [Fact]
    public void TestTraceHistoryQueryGrain()
    {
        var history = CreateHistory();

        Add(history, 100);

        var grainDictionary = history.QueryGrain("GRAIN1");

        Assert.Single(grainDictionary.Keys);
        Assert.Equal("GRAIN1.METHOD1", grainDictionary.Keys.First());
        Assert.Equal(100, grainDictionary["GRAIN1.METHOD1"].Keys.Count);
    }

    [Fact]
    public void TestTraceHistoryGroupByGrainAndSilo()
    {
        var history = CreateHistory();

        Add(history, 100);

        var traceAggregate = history.GroupByGrainAndSilo().ToList();

        Assert.Equal(2, traceAggregate.Count);

        var silo1Aggregate = traceAggregate.FirstOrDefault(x => x.SiloAddress == "SILO1");

        Assert.Equal("SILO1", silo1Aggregate.SiloAddress);
        Assert.Equal("GRAIN1", silo1Aggregate.Grain);
        Assert.Equal(100, silo1Aggregate.Count);
        Assert.Equal(0, silo1Aggregate.ExceptionCount);
        Assert.Equal(1000, silo1Aggregate.ElapsedTime);

        var silo2Aggregate = traceAggregate.FirstOrDefault(x => x.SiloAddress == "SILO2");

        Assert.Equal("SILO2", silo2Aggregate.SiloAddress);
        Assert.Equal("GRAIN1", silo2Aggregate.Grain);
        Assert.Equal(100 * 100, silo2Aggregate.Count);
        Assert.Equal(10 * 100, silo2Aggregate.ExceptionCount);
        Assert.Equal(200 * 100, silo2Aggregate.ElapsedTime);
    }


    [Fact]
    public void TestTraceHistoryTopGrainMethods()
    {
        var history = CreateHistory();

        Add(history, 100);

        var results = history.AggregateByGrainMethod().ToList();
        var result = results.First();

        Assert.Single(results);
        Assert.Equal(100 + (100 * 100), result.Count);
        Assert.Equal(10 * 100, result.ExceptionCount);
        Assert.Equal(1000 + (200 * 100), result.ElapsedTime);
    }
}
