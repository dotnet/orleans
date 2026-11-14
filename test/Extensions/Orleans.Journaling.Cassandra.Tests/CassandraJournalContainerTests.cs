using System.Reflection;
using Cassandra;
using DotNet.Testcontainers.Containers;
using Xunit;

namespace Tester.Cassandra.Journaling;

[TestSuite("BVT")]
[TestProvider("Cassandra")]
[TestArea("Journaling")]
[TestCategory("BVT")]
[TestCategory("Cassandra")]
public sealed class CassandraJournalContainerTests
{
    [Theory]
    [InlineData(null, "cassandra:5.0")]
    [InlineData("", "cassandra:5.0")]
    [InlineData(" ", "cassandra:5.0")]
    [InlineData("4.1", "cassandra:4.1")]
    public void GetImageNameUsesDefaultVersionWhenUnset(string? version, string expected)
        => Assert.Equal(expected, CassandraJournalContainer.GetImageName(version));

    [Fact]
    public async Task DisposeAsyncWaitsForPendingStartupAndDisposesItsContainer()
    {
        var backendCompletion = new TaskCompletionSource<(IContainer Container, Cluster Cluster, ISession Session)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var container = DispatchProxy.Create<IContainer, TestContainerProxy>();
        var containerProxy = (TestContainerProxy)(object)container;
        var cluster = Cluster.Builder().AddContactPoint("127.0.0.1").Build();
        var session = DispatchProxy.Create<ISession, TestSessionProxy>();
        var fixture = new CassandraJournalContainer(_ => backendCompletion.Task);
        Task? disposeTask = null;

        try
        {
            using var cancellationTokenSource = new CancellationTokenSource();
            var sessionTask = fixture.GetSessionAsync(cancellationTokenSource.Token);
            cancellationTokenSource.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sessionTask);

            disposeTask = fixture.DisposeAsync().AsTask();
            Assert.False(disposeTask.IsCompleted);
        }
        finally
        {
            backendCompletion.TrySetResult((container, cluster, session));
            await (disposeTask ?? fixture.DisposeAsync().AsTask());
        }

        Assert.True(containerProxy.DisposeCalled);
    }

    public class TestContainerProxy : DispatchProxy
    {
        public bool DisposeCalled { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IContainer.DisposeAsync))
            {
                DisposeCalled = true;
                return ValueTask.CompletedTask;
            }

            throw new NotSupportedException($"Unexpected container member: {targetMethod?.Name}");
        }
    }

    public class TestSessionProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => throw new NotSupportedException($"Unexpected session member: {targetMethod?.Name}");
    }
}
