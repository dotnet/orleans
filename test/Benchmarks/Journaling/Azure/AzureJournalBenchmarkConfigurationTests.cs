using BenchmarkDotNet.Configs;
using BenchmarkDotNet.ConsoleArguments;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Validators;
using TestExtensions;
using Xunit;

namespace Benchmarks.Journaling.Azure;

[TestCategory("BVT")]
public class AzureJournalBenchmarkConfigurationTests
{
    [Theory]
    [InlineData("default")]
    [InlineData("dry")]
    [InlineData("short")]
    [InlineData("medium")]
    [InlineData("long")]
    public void BdnResolvedPresetsHaveFiniteWorkLimits(string preset)
    {
        var parsed = ConfigParser.Parse(["--job", preset, "--inProcess"], NullLogger.Instance);
        Assert.True(parsed.isSuccess);
        using var benchmarks = BenchmarkConverter.TypeToBenchmarks(typeof(AzureJournalBenchmarks), parsed.config);
        Assert.NotEmpty(benchmarks.BenchmarksCases);
        Assert.Single(benchmarks.Config.GetValidators().OfType<AzureJournalBenchmarkValidator>());
        Assert.Empty(new AzureJournalBenchmarkValidator().Validate(benchmarks));
        foreach (var benchmark in benchmarks.BenchmarksCases)
        {
            Assert.InRange(benchmark.Job.Run.IterationCount, 1, 3);
            Assert.InRange(benchmark.Job.Run.WarmupCount, 0, 1);
            Assert.Equal(1, benchmark.Job.Run.InvocationCount);
            Assert.Equal(1, benchmark.Job.Run.UnrollFactor);
            Assert.Equal(1, benchmark.Job.Run.LaunchCount);
        }
    }

    [Theory]
    [InlineData("--iterationCount", "100")]
    [InlineData("--warmupCount", "100")]
    [InlineData("--launchCount", "100")]
    [InlineData("--invocationCount", "100")]
    [InlineData("--unrollFactor", "100")]
    public void BdnCliOverridesAreBoundedOrRejectedBeforeExecution(string name, string value)
    {
        var parsed = ConfigParser.Parse(["--job", "Dry", name, value], NullLogger.Instance);
        Assert.True(parsed.isSuccess);
        using var benchmarks = BenchmarkConverter.TypeToBenchmarks(typeof(AzureJournalBenchmarks), parsed.config);
        var validator = Assert.Single(benchmarks.Config.GetValidators().OfType<AzureJournalBenchmarkValidator>());
        var errors = validator.Validate(benchmarks).ToArray();
        if (errors.Length > 0)
        {
            Assert.All(errors, error => Assert.True(error.IsCritical));
        }
        else
        {
            Assert.All(benchmarks.BenchmarksCases, benchmark =>
            {
                Assert.InRange(benchmark.Job.Run.IterationCount, 1, 3);
                Assert.InRange(benchmark.Job.Run.WarmupCount, 0, 1);
                Assert.Equal(1, benchmark.Job.Run.InvocationCount);
                Assert.Equal(1, benchmark.Job.Run.UnrollFactor);
                Assert.Equal(1, benchmark.Job.Run.LaunchCount);
            });
        }
    }

    [Theory]
    [InlineData("iterations")]
    [InlineData("warmup")]
    [InlineData("launches")]
    [InlineData("invocations")]
    [InlineData("unroll")]
    [InlineData("adaptive")]
    public void BdnValidatorRejectsUnsafeFinalJobs(string change)
    {
        using var benchmarks = BenchmarkConverter.TypeToBenchmarks(typeof(AzureJournalBenchmarks));
        var original = benchmarks.BenchmarksCases[0];
        var job = change switch
        {
            "iterations" => original.Job.WithIterationCount(100),
            "warmup" => original.Job.WithWarmupCount(100),
            "launches" => original.Job.WithLaunchCount(100),
            "invocations" => original.Job.WithInvocationCount(100),
            "unroll" => original.Job.WithUnrollFactor(100),
            _ => Job.Default
        };
        var candidate = BenchmarkCase.Create(original.Descriptor, job, original.Parameters, original.Config);
        var error = Assert.Single(new AzureJournalBenchmarkValidator().Validate(
            new ValidationParameters([candidate], original.Config)));
        Assert.True(error.IsCritical);
        Assert.Same(candidate, error.BenchmarkCase);
        Assert.Contains("Azure journal benchmarks require", error.Message);
    }
}
