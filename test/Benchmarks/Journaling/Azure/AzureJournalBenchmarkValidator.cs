using BenchmarkDotNet.Characteristics;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Environments;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Validators;

namespace Benchmarks.Journaling.Azure;

internal sealed class AzureJournalBenchmarkValidator : IValidator
{
    public bool TreatsWarningsAsErrors => true;

    public IEnumerable<ValidationError> Validate(ValidationParameters validationParameters)
    {
        var resolver = new CompositeResolver(EnvironmentResolver.Instance, EngineResolver.Instance);
        foreach (var benchmark in validationParameters.Benchmarks)
        {
            if (benchmark.Descriptor.Type != typeof(AzureJournalBenchmarks))
            {
                continue;
            }

            var run = benchmark.Job.Run;
            if (!run.HasValue(RunMode.IterationCountCharacteristic) || run.IterationCount is < 1 or > 3
                || !run.HasValue(RunMode.WarmupCountCharacteristic) || run.WarmupCount is < 0 or > 1
                || !run.HasValue(RunMode.InvocationCountCharacteristic) || run.InvocationCount != 1
                || run.ResolveValue(RunMode.UnrollFactorCharacteristic, resolver) != 1
                || !run.HasValue(RunMode.LaunchCountCharacteristic) || run.LaunchCount != 1)
            {
                yield return new ValidationError(true,
                    "Azure journal benchmarks require 1-3 measured iterations, 0-1 warmup iterations, one invocation per iteration, unroll factor 1, and one launch. Use Journaling.Azure for larger fixed-work runs.",
                    benchmark);
            }
        }
    }
}
