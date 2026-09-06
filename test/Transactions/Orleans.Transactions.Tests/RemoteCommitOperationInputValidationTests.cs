using Orleans.Transactions.TestKit;
using TestExtensions;
using Xunit;

namespace Orleans.Transactions.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Transactions")]
[TestCategory("BVT"), TestCategory("Transactions")]
public class RemoteCommitOperationInputValidationTests
{
    [Fact]
    public async Task PassOperation_Commit_NullService_ThrowsWithServiceParamName()
    {
        var operation = new PassOperation("pass-null-service");

        var commitTask = operation.Commit(
            new Guid("72D8A038-64D8-431D-8592-23D38522F914"),
            null!);
        var exception = await Assert.ThrowsAsync<ArgumentNullException>(() => commitTask);

        Assert.Equal("service", exception.ParamName);
        Assert.Equal("pass-null-service", operation.Data);
    }

    [Fact]
    public async Task FailOperation_Commit_NullService_ThrowsWithServiceParamName()
    {
        var operation = new FailOperation("fail-null-service");

        var commitTask = operation.Commit(
            new Guid("E0DD10C9-213F-4D2D-97D9-B0FC030A5D87"),
            null!);
        var exception = await Assert.ThrowsAsync<ArgumentNullException>(() => commitTask);

        Assert.Equal("service", exception.ParamName);
        Assert.Equal("fail-null-service", operation.Data);
    }

    [Fact]
    public async Task ThrowOperation_Commit_NullService_ThrowsWithServiceParamName()
    {
        var operation = new ThrowOperation("throw-null-service");

        var commitTask = operation.Commit(
            new Guid("8E4CF4DD-082F-4DD6-8342-46F2BA30C8D7"),
            null!);
        var exception = await Assert.ThrowsAsync<ArgumentNullException>(() => commitTask);

        Assert.Equal("service", exception.ParamName);
        Assert.Equal("throw-null-service", operation.Data);
    }

    [Fact]
    public async Task PassOperation_Commit_ForwardsExactTransactionIdAndDataAndReturnsOutcome()
    {
        var transactionId = new Guid("25816EE5-5DFC-47F2-AC99-9C14372F906B");
        var data = new string("pass-operation-data".ToCharArray());
        var operation = new PassOperation(data);
        var service = new RecordingRemoteCommitService();

        var result = await operation.Commit(transactionId, service);

        Assert.True(result);
        Assert.Equal(1, service.PassCallCount);
        Assert.Equal(0, service.FailCallCount);
        Assert.Equal(0, service.ThrowCallCount);
        Assert.Equal(transactionId, service.LastTransactionId);
        Assert.Equal(data, service.LastData);
        Assert.Same(data, service.LastData);
        Assert.Equal(RemoteCommitMethod.Pass, service.LastMethod);
    }

    [Fact]
    public async Task FailOperation_Commit_ForwardsExactTransactionIdAndDataAndReturnsOutcome()
    {
        var transactionId = new Guid("340AD8DE-6F8D-4BB9-9E5B-2D6FD6DCE80C");
        var data = new string("fail-operation-data".ToCharArray());
        var operation = new FailOperation(data);
        var service = new RecordingRemoteCommitService();

        var result = await operation.Commit(transactionId, service);

        Assert.False(result);
        Assert.Equal(0, service.PassCallCount);
        Assert.Equal(1, service.FailCallCount);
        Assert.Equal(0, service.ThrowCallCount);
        Assert.Equal(transactionId, service.LastTransactionId);
        Assert.Equal(data, service.LastData);
        Assert.Same(data, service.LastData);
        Assert.Equal(RemoteCommitMethod.Fail, service.LastMethod);
    }

    [Fact]
    public async Task ThrowOperation_Commit_ForwardsExactTransactionIdAndDataAndPropagatesFailure()
    {
        var transactionId = new Guid("9B7345CB-0186-40BA-BE23-13A44684A24D");
        var data = new string("throw-operation-data".ToCharArray());
        var operation = new ThrowOperation(data);
        var expectedException = new InvalidOperationException("remote commit failed");
        var service = new RecordingRemoteCommitService(expectedException);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => operation.Commit(transactionId, service));

        Assert.Same(expectedException, exception);
        Assert.Equal(0, service.PassCallCount);
        Assert.Equal(0, service.FailCallCount);
        Assert.Equal(1, service.ThrowCallCount);
        Assert.Equal(transactionId, service.LastTransactionId);
        Assert.Equal(data, service.LastData);
        Assert.Same(data, service.LastData);
        Assert.Equal(RemoteCommitMethod.Throw, service.LastMethod);
    }

    private enum RemoteCommitMethod
    {
        None,
        Pass,
        Fail,
        Throw,
    }

    private sealed class RecordingRemoteCommitService : IRemoteCommitService
    {
        private readonly Exception? exception;

        public RecordingRemoteCommitService(Exception? exception = null)
        {
            this.exception = exception;
        }

        public int PassCallCount { get; private set; }

        public int FailCallCount { get; private set; }

        public int ThrowCallCount { get; private set; }

        public Guid LastTransactionId { get; private set; }

        public string? LastData { get; private set; }

        public RemoteCommitMethod LastMethod { get; private set; }

        public Task<bool> Pass(Guid transactionId, string data)
        {
            PassCallCount++;
            Record(RemoteCommitMethod.Pass, transactionId, data);
            return Task.FromResult(true);
        }

        public Task<bool> Fail(Guid transactionId, string data)
        {
            FailCallCount++;
            Record(RemoteCommitMethod.Fail, transactionId, data);
            return Task.FromResult(false);
        }

        public Task<bool> Throw(Guid transactionId, string data)
        {
            ThrowCallCount++;
            Record(RemoteCommitMethod.Throw, transactionId, data);
            return Task.FromException<bool>(
                exception ?? new InvalidOperationException("Unexpected throw invocation."));
        }

        private void Record(RemoteCommitMethod method, Guid transactionId, string data)
        {
            LastMethod = method;
            LastTransactionId = transactionId;
            LastData = data;
        }
    }
}
