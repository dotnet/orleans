using Orleans.Transactions.TestKit;
using TestExtensions;
using Xunit;

namespace Orleans.Transactions.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Transactions")]
[TestCategory("BVT"), TestCategory("Transactions")]
public class ExclusiveLockCoordinatorInputValidationTests
{
    [Fact]
    public async Task ReadThenWrite_NullGrain_ThrowsWithGrainParamNameBeforeGetOrAdd()
    {
        var coordinator = new ExclusiveLockCoordinatorGrain();

        var exception = await Assert.ThrowsAsync<ArgumentNullException>(
            () => coordinator.ReadThenWrite(null!, 43));

        Assert.Equal("grain", exception.ParamName);
    }

    [Fact]
    public async Task ReadThenWriteWithExclusiveLock_NullGrain_ThrowsBeforeGetDelayOrAdd()
    {
        var coordinator = new ExclusiveLockCoordinatorGrain();

        var exception = await Assert.ThrowsAsync<ArgumentNullException>(
            () => coordinator.ReadThenWriteWithExclusiveLock(null!, 47));

        Assert.Equal("grain", exception.ParamName);
    }

    [Theory]
    [InlineData(nameof(ExclusiveLockCoordinatorGrain.ReadThenWrite))]
    [InlineData(nameof(ExclusiveLockCoordinatorGrain.ReadThenWriteWithExclusiveLock))]
    public async Task ReadThenWriteMethods_ValidGrain_CallGetBeforeAddWithExactValue(
        string method)
    {
        var events = new List<string>();
        var grain = new RecordingTransactionTestGrain(events);
        var coordinator = new ExclusiveLockCoordinatorGrain();

        switch (method)
        {
            case nameof(ExclusiveLockCoordinatorGrain.ReadThenWrite):
                await coordinator.ReadThenWrite(grain, 53);
                break;
            case nameof(ExclusiveLockCoordinatorGrain.ReadThenWriteWithExclusiveLock):
                await coordinator.ReadThenWriteWithExclusiveLock(grain, 53);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(method), method, null);
        }

        Assert.Equal(new[] { "Get", "Add(53)" }, events);
        Assert.Equal(1, grain.GetCallCount);
        Assert.Equal(1, grain.AddCallCount);
        Assert.Equal(53, grain.LastAddedValue);
    }

    private sealed class RecordingTransactionTestGrain :
        ITransactionTestGrain,
        IExclusiveLockTransactionTestGrain
    {
        private readonly List<string> events;

        public RecordingTransactionTestGrain(List<string> events)
        {
            this.events = events;
        }

        public int GetCallCount { get; private set; }

        public int AddCallCount { get; private set; }

        public int LastAddedValue { get; private set; }

        public Task<int[]> Get()
        {
            GetCallCount++;
            events.Add("Get");
            return Task.FromResult(new[] { 59 });
        }

        public Task<int[]> Add(int numberToAdd)
        {
            AddCallCount++;
            LastAddedValue = numberToAdd;
            events.Add($"Add({numberToAdd})");
            return Task.FromResult(new[] { 59 + numberToAdd });
        }

        public Task Set(int newValue) => UnexpectedCall(nameof(Set));

        public Task AddAndThrow(int numberToAdd) => UnexpectedCall(nameof(AddAndThrow));

        public Task SetAndThrow(int numberToSet) => UnexpectedCall(nameof(SetAndThrow));

        public Task Deactivate() => UnexpectedCall(nameof(Deactivate));

        private static Task UnexpectedCall(string method) =>
            Task.FromException(new InvalidOperationException($"Unexpected {method} call."));
    }
}
