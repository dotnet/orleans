using Orleans.Transactions.Abstractions;
using Orleans.Transactions.TestKit;
using TestExtensions;
using Xunit;

namespace Orleans.Transactions.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Transactions")]
[TestCategory("BVT"), TestCategory("Transactions")]
public class TransactionCoordinatorInputValidationTests
{
    [Fact]
    public async Task AddAndThrow_NullGrain_ThrowsWithGrainParamNameBeforeGrainCall()
    {
        var coordinator = new TransactionCoordinatorGrain();

        var exception = await Assert.ThrowsAsync<ArgumentNullException>(
            () => coordinator.AddAndThrow(null!, 17));

        Assert.Equal("grain", exception.ParamName);
    }

    [Fact]
    public void MultiGrainAddWithCommitter_NullCommitter_ThrowsBeforeEnumeratingOrUpdatingGrains()
    {
        var events = new List<string>();
        var firstGrain = new RecordingTransactionTestGrain("first", events);
        var secondGrain = new RecordingTransactionTestGrain("second", events);
        var grains = new List<ITransactionTestGrain> { firstGrain, secondGrain };
        var operation = new RecordingCommitOperation();
        var coordinator = new TransactionCoordinatorGrain();

        var exception = Assert.Throws<ArgumentNullException>(() =>
        {
            _ = coordinator.MultiGrainAdd(null!, operation, grains, 23);
        });
        var allNullException = Assert.Throws<ArgumentNullException>(() =>
        {
            _ = coordinator.MultiGrainAdd(null!, null!, null!, 23);
        });

        Assert.Equal("committer", exception.ParamName);
        Assert.Equal("committer", allNullException.ParamName);
        Assert.Empty(events);
        Assert.Equal(0, firstGrain.AddCallCount);
        Assert.Equal(0, secondGrain.AddCallCount);
        Assert.Equal(0, operation.CommitCallCount);
    }

    [Fact]
    public async Task UpdateViolated_NullGrain_ThrowsWithGrainParamNameBeforeGrainCall()
    {
        var coordinator = new TransactionCoordinatorGrain();

        var exception = await Assert.ThrowsAsync<ArgumentNullException>(
            () => coordinator.UpdateViolated(null!, 29));

        Assert.Equal("grain", exception.ParamName);
    }

    [Fact]
    public async Task MultiGrainAddWithCommitter_ValidInputs_EnlistsCommitAndFansOutExactValue()
    {
        var events = new List<string>();
        var firstGrain = new RecordingTransactionTestGrain("first", events);
        var secondGrain = new RecordingTransactionTestGrain("second", events);
        var grains = new List<ITransactionTestGrain> { firstGrain, secondGrain };
        var committer = new RecordingCommitterTestGrain(events);
        var operation = new RecordingCommitOperation();
        var coordinator = new TransactionCoordinatorGrain();

        await coordinator.MultiGrainAdd(committer, operation, grains, 31);

        Assert.Equal(
            new[] { "first:Add(31)", "second:Add(31)", "Commit" },
            events);
        Assert.Equal(1, firstGrain.AddCallCount);
        Assert.Equal(1, secondGrain.AddCallCount);
        Assert.Equal(31, firstGrain.LastAddedValue);
        Assert.Equal(31, secondGrain.LastAddedValue);
        Assert.Equal(1, committer.CommitCallCount);
        Assert.Same(operation, committer.LastOperation);
        Assert.Equal(0, operation.CommitCallCount);
    }

    [Fact]
    public async Task AddAndThrow_ValidGrain_PerformsExpectedUpdateBeforePropagatingAbortFailure()
    {
        var events = new List<string>();
        var grain = new RecordingTransactionTestGrain("target", events);
        var coordinator = new TransactionCoordinatorGrain();

        var exception = await Assert.ThrowsAsync<Exception>(
            () => coordinator.AddAndThrow(grain, 37));

        Assert.Equal("This should abort the transaction", exception.Message);
        Assert.Equal(new[] { "target:Add(37)" }, events);
        Assert.Equal(1, grain.AddCallCount);
        Assert.Equal(37, grain.LastAddedValue);
    }

    [Fact]
    public async Task UpdateViolated_ValidGrain_ForwardsTheExactValue()
    {
        var events = new List<string>();
        var grain = new RecordingTransactionTestGrain("target", events);
        var coordinator = new TransactionCoordinatorGrain();

        await coordinator.UpdateViolated(grain, 41);

        Assert.Equal(new[] { "target:Add(41)" }, events);
        Assert.Equal(1, grain.AddCallCount);
        Assert.Equal(41, grain.LastAddedValue);
    }

    private sealed class RecordingTransactionTestGrain : ITransactionTestGrain
    {
        private readonly string name;
        private readonly List<string> events;

        public RecordingTransactionTestGrain(string name, List<string> events)
        {
            this.name = name;
            this.events = events;
        }

        public int AddCallCount { get; private set; }

        public int LastAddedValue { get; private set; }

        public Task<int[]> Add(int numberToAdd)
        {
            AddCallCount++;
            LastAddedValue = numberToAdd;
            events.Add($"{name}:Add({numberToAdd})");
            return Task.FromResult(new[] { numberToAdd });
        }

        public Task Set(int newValue) => UnexpectedCall(nameof(Set));

        public Task<int[]> Get() => UnexpectedCall<int[]>(nameof(Get));

        public Task AddAndThrow(int numberToAdd) => UnexpectedCall(nameof(AddAndThrow));

        public Task SetAndThrow(int numberToSet) => UnexpectedCall(nameof(SetAndThrow));

        public Task Deactivate() => UnexpectedCall(nameof(Deactivate));

        private static Task UnexpectedCall(string method) =>
            Task.FromException(new InvalidOperationException($"Unexpected {method} call."));

        private static Task<T> UnexpectedCall<T>(string method) =>
            Task.FromException<T>(new InvalidOperationException($"Unexpected {method} call."));
    }

    private sealed class RecordingCommitterTestGrain : ITransactionCommitterTestGrain
    {
        private readonly List<string> events;

        public RecordingCommitterTestGrain(List<string> events)
        {
            this.events = events;
        }

        public int CommitCallCount { get; private set; }

        public ITransactionCommitOperation<IRemoteCommitService>? LastOperation { get; private set; }

        public Task Commit(ITransactionCommitOperation<IRemoteCommitService> operation)
        {
            CommitCallCount++;
            LastOperation = operation;
            events.Add("Commit");
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingCommitOperation : ITransactionCommitOperation<IRemoteCommitService>
    {
        public int CommitCallCount { get; private set; }

        public Task<bool> Commit(Guid transactionId, IRemoteCommitService service)
        {
            CommitCallCount++;
            return Task.FromResult(true);
        }
    }
}
