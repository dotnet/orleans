using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Accordant;
using Xunit;
using static Orleans.Clustering.TestKit.MembershipTableModelBasedTestRunner;

namespace Orleans.Clustering.TestKit.Tests;

public sealed class MembershipTableModelBasedPartitionTests
{
    private static readonly int[] OriginalOperationCounts = [412, 500, 58, 442, 629, 58, 58, 49, 1, 1, 58, 36, 1, 1, 2, 2, 65, 500];

    [Fact]
    public void Manifest_DefaultGenerationPreservesOriginal959Cases()
    {
        var batches = CreateRunner().GenerateBatches(TestContext.Current.CancellationToken);
        var original = GenerateOriginalCases();
        var cases = batches.SelectMany(batch => batch.Cases).ToArray();

        Assert.Equal(959, cases.Length);
        Assert.Equal(8, batches.Count);
        Assert.Equal(Enumerable.Range(0, 959), cases.Select(testCase => testCase.Index));
        Assert.Equal(959, cases.Select(testCase => testCase.Identity).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(original.Select(Describe), cases.Select(testCase => Describe(testCase.TestCase)));
        Assert.Equal(OriginalOperationCounts, Counts(original));
        Assert.Equal(OriginalOperationCounts, Counts(cases.Select(testCase => testCase.TestCase)));
        Assert.All(Counts(original), count => Assert.True(count > 0));
        Assert.Equal(18, Counts(original).Length);
        Assert.Same(batches, SelectPartition(batches, 0, 1));
        foreach (var batch in batches)
        {
            Assert.Equal(batch.TestCases.Count, batch.Cases.Count);
            for (var index = 0; index < batch.Cases.Count; index++)
                Assert.Same(batch.TestCases[index], batch.Cases[index].TestCase);
        }
    }

    [Fact]
    public void Partitions_PreserveDuplicateTraceOccurrencesWithinAndAcrossBatches()
    {
        var original = CreateRunner().GenerateBatches(TestContext.Current.CancellationToken)[0];
        var repeated = CreateRunner().GenerateBatches(TestContext.Current.CancellationToken)[0];
        var first = original.TestCases[0];
        var duplicate = repeated.TestCases[0];
        Assert.NotSame(first, duplicate);
        Assert.Equal(Describe(first), Describe(duplicate));
        var cases = new[] { first, duplicate };
        var firstBatch = original with { TestCases = cases, Cases = CreateCaseManifest(0, 0, cases) };
        var secondBatch = repeated with { TestCases = cases, Cases = CreateCaseManifest(1, 2, cases) };
        var batches = new[] { firstBatch, secondBatch };
        var manifest = batches.SelectMany(batch => batch.Cases).ToArray();
        Assert.Equal(4, manifest.Select(testCase => testCase.Identity).Distinct(StringComparer.Ordinal).Count());
        Assert.EndsWith("|0000", manifest[0].Identity);
        Assert.EndsWith("|0001", manifest[1].Identity);
        var selected = Enumerable.Range(0, 4).Select(index => Assert.Single(SelectPartition(batches, index, 4).SelectMany(batch => batch.Cases))).ToArray();
        Assert.Equal(manifest.Select(testCase => testCase.Identity).Order(StringComparer.Ordinal),
            selected.Select(testCase => testCase.Identity).Order(StringComparer.Ordinal));
        Assert.Equal(Counts(manifest.Select(testCase => testCase.TestCase)), Counts(selected.Select(testCase => testCase.TestCase)));
        foreach (var testCase in selected)
            Assert.Same(Assert.Single(manifest, item => item.Identity == testCase.Identity).TestCase, testCase.TestCase);
    }

    [Fact]
    public void Partitions_AreDisjointExhaustiveBalancedAndRetainOriginalCaseObjects()
    {
        var batches = CreateRunner().GenerateBatches(TestContext.Current.CancellationToken);
        var original = batches.SelectMany(batch => batch.Cases).ToDictionary(testCase => testCase.Identity, StringComparer.Ordinal);
        var union = new List<GeneratedCase>();
        var counts = new List<int>();
        for (var partitionIndex = 0; partitionIndex < 4; partitionIndex++)
        {
            var selected = SelectPartition(batches, partitionIndex, 4);
            var cases = selected.SelectMany(batch => batch.Cases).ToArray();
            counts.Add(cases.Length);
            Assert.Equal(cases.Select(testCase => testCase.Index).Order(), cases.Select(testCase => testCase.Index));
            foreach (var testCase in cases)
            {
                Assert.Same(original[testCase.Identity], testCase);
                Assert.Same(original[testCase.Identity].TestCase, testCase.TestCase);
            }
            foreach (var batch in selected)
            {
                var originalBatch = Assert.Single(batches, candidate => ReferenceEquals(candidate.Spec, batch.Spec));
                Assert.Same(originalBatch.InitialState, batch.InitialState);
                Assert.Equal(batch.Cases.Count, batch.TestCases.Count);
                for (var index = 0; index < batch.Cases.Count; index++)
                    Assert.Same(batch.Cases[index].TestCase, batch.TestCases[index]);
            }
            union.AddRange(cases);
        }

        Assert.Equal([240, 240, 240, 239], counts);
        Assert.Equal(959, union.Select(testCase => testCase.Identity).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(original.Keys.Order(StringComparer.Ordinal), union.Select(testCase => testCase.Identity).Order(StringComparer.Ordinal));
        Assert.Equal(Counts(original.Values.Select(testCase => testCase.TestCase)), Counts(union.Select(testCase => testCase.TestCase)));
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("tr-TR")]
    [InlineData("ar-SA")]
    public void Partitions_AreDeterministicAcrossCulturesAndPinnedManifest(string culture)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            var first = CreateRunner().GenerateBatches(TestContext.Current.CancellationToken);
            var second = CreateRunner().GenerateBatches(TestContext.Current.CancellationToken);
            var fingerprints = new List<string>();
            for (var index = 0; index < 4; index++)
            {
                var selected = SelectPartition(first, index, 4).SelectMany(batch => batch.Cases).Select(testCase => testCase.Identity).ToArray();
                Assert.Equal(selected, SelectPartition(second, index, 4).SelectMany(batch => batch.Cases).Select(testCase => testCase.Identity));
                fingerprints.Add(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", selected.Order(StringComparer.Ordinal))))));
            }
            Assert.Equal(
            [
                "DAF3111806D9D6F5FC80357B6DC39F8BF3C2F5B120D7B7D777749CAD54E660BE",
                "2E990EB527810CC06D181B72F940BB9E6142AC5DB507EEB38CC0ACF6993E1A06",
                "972C48E20CA0FBD725171D97486859F9F45153FF288F263AE55D25A400611EA8",
                "0B499F803E8BF40B92A94F0F819A824A3F9A554C86A6F0E40CB4C60FED7C51B0"
            ], fingerprints);
            Assert.Equal(OriginalOperationCounts, Counts(first.SelectMany(batch => batch.TestCases)));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData(-1, 959)]
    [InlineData(0, 240)]
    [InlineData(1, 240)]
    [InlineData(2, 240)]
    [InlineData(3, 239)]
    public async Task Run_PublicFullAndPartitionsExecuteExpectedCasesAndOperationCounts(int partitionIndex, int expectedCount)
    {
        var backend = new IdealizedMembershipBackend { LagHeartbeatReads = true };
        var messages = new List<string>();
        var scopes = new HashSet<string>(StringComparer.Ordinal);
        var runner = new MembershipTableModelBasedTestRunner(() =>
        {
            var fixture = backend.Fixture();
            Assert.True(scopes.Add(fixture.ClusterId));
            Assert.True(scopes.Add(fixture.OtherClusterId));
            return fixture;
        }, new MembershipTableModelBasedConformanceOptions { ProviderName = "partitioned", Seed = 17 }, messages.Add);
        var batches = runner.GenerateBatches(TestContext.Current.CancellationToken);
        var selected = partitionIndex < 0 ? batches : SelectPartition(batches, partitionIndex, 4);
        var expected = selected.SelectMany(batch => batch.Cases).ToArray();
        Assert.Equal(expectedCount, expected.Length);

        if (partitionIndex < 0)
            await runner.RunGeneratedConformanceTests(TestContext.Current.CancellationToken);
        else
            await runner.RunGeneratedConformanceTests(partitionIndex, 4, TestContext.Current.CancellationToken);

        var executed = messages.Select(message => Regex.Match(message, @"^seed=17; case=(\d+); phase=initialize;"))
            .Where(match => match.Success).Select(match => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture));
        Assert.Equal(expected.Select(testCase => testCase.Index), executed);
        var operations = messages.Where(message => message.EndsWith("; phase=completed", StringComparison.Ordinal))
            .Select(message => Enum.Parse<MembershipOperationKind>(Regex.Match(message, @"operation=(\w+)\(").Groups[1].Value)).ToArray();
        Assert.Equal(Counts(expected.Select(testCase => testCase.TestCase)),
            Enum.GetValues<MembershipOperationKind>().Select(kind => operations.Count(operation => operation == kind)).ToArray());
        Assert.Equal(expectedCount * 2, scopes.Count);
        Assert.Equal(expectedCount * 3, backend.CreatedHandles);
        Assert.Equal(backend.CreatedHandles, backend.DisposedHandles);
        Assert.Empty(backend.Partitions);
        var summary = Assert.Single(messages, message => message.Contains("Accordant cases=", StringComparison.Ordinal));
        Assert.Contains($"Accordant cases={expectedCount};", summary, StringComparison.Ordinal);
        Assert.Contains(partitionIndex < 0 ? "partition=0/1;" : $"partition={partitionIndex}/4;", summary, StringComparison.Ordinal);
        Assert.Contains("operation-counts=", summary, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(-1, 4, "partitionIndex")]
    [InlineData(4, 4, "partitionIndex")]
    [InlineData(0, 0, "partitionCount")]
    [InlineData(0, -1, "partitionCount")]
    [InlineData(0, 960, "partitionCount")]
    public async Task Run_InvalidPartitionFailsBeforeFactory(int partitionIndex, int partitionCount, string parameter)
    {
        var calls = 0;
        var runner = new MembershipTableModelBasedTestRunner(() =>
        {
            calls++;
            return new IdealizedMembershipBackend().Fixture();
        }, "invalid-partition");
        var exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => runner.RunGeneratedConformanceTests(partitionIndex, partitionCount, TestContext.Current.CancellationToken));
        Assert.Equal(parameter, exception.ParamName);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Run_PreCancelledPartitionDoesNotAcquireFixture()
    {
        var calls = 0;
        var runner = new MembershipTableModelBasedTestRunner(() =>
        {
            calls++;
            return new IdealizedMembershipBackend().Fixture();
        }, "cancelled-partition");
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunGeneratedConformanceTests(2, 4, canceled.Token));
        Assert.Equal(0, calls);
    }

    private static MembershipTableModelBasedTestRunner CreateRunner() => new(
        () => throw new InvalidOperationException("Manifest generation must not acquire a fixture."),
        new MembershipTableModelBasedConformanceOptions { ProviderName = "manifest", Seed = 17 });

    private static string Describe(SequentialTestCase testCase) => string.Join(";", testCase.OperationCalls.Select(call =>
    {
        var request = (MembershipRequest)call.OperationInput.Request;
        return FormattableString.Invariant($"{call.OperationInput.Operation.Name}:{(int)request.Kind}:{request.Key}");
    }));

    private static int[] Counts(IEnumerable<SequentialTestCase> cases)
    {
        var requests = cases.SelectMany(testCase => testCase.OperationCalls)
            .Select(call => ((MembershipRequest)call.OperationInput.Request).Kind).ToArray();
        return Enum.GetValues<MembershipOperationKind>().Select(kind => requests.Count(request => request == kind)).ToArray();
    }

    private static SequentialTestCase[] GenerateOriginalCases()
    {
        var result = new List<SequentialTestCase>();
        Generate(null);
        foreach (var prefix in RequiredPrefixes()) Generate(prefix);
        return result.ToArray();

        void Generate(MembershipRequest[]? prefix)
        {
            var spec = new MembershipBehavioralSpec();
            result.AddRange(spec.GenerateTests(new MembershipModelState(), spec.CreateInputSet(prefix?.Distinct()), new TestGenerationOptions
            {
                MaxDepth = prefix is null ? 3 : prefix.Length + 1,
                SequentialTestCaseAlgorithm = SequentialTestCaseAlgorithms.CreateTransitionCoverage(prefix?.Length ?? 3),
                ShouldApply = (input, state) =>
                {
                    var request = (MembershipRequest)input.Request;
                    var model = (MembershipModelState)state;
                    return MembershipModel.CanApply(request, model)
                        && (prefix is null || (model.Steps < prefix.Length && prefix[model.Steps] == request));
                }
            }));
        }
    }
}
