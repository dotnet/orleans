using Documentation.Diagnostics.DurableTasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.DurableTasks;
using Xunit;

namespace Microsoft.Orleans.DurableTasks.IntegrationTests;

[Collection(DurableTaskRecoveryCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
public sealed class DurableTaskDiagnosticSnippetTests : IAsyncLifetime
{
    private readonly EmailSender _email = new();
    private readonly DurableTaskRecoveryFixture _fixture;

    public DurableTaskDiagnosticSnippetTests()
    {
        _fixture = new(DurableTaskRecoveryFixture.OrleansBinary, silo => silo.Services.AddSingleton<IEmailSender>(_email));
    }

    public ValueTask InitializeAsync() => _fixture.InitializeAsync();
    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    [Fact]
    public async Task Orleans0219DetachedGrainCallSendsNotificationAfterShipmentCompletes()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var grain = _fixture.Client.GetGrain<INotificationGrain>(Guid.NewGuid().ToString("N"));
        const string customer = "customer@example.com";

        var shipment = await grain.ShipThenNotifyAsync(customer).WithId("ship").ScheduleAsync(cancellationToken);
        Assert.Equal(DurableTaskStatus.CompletedSuccessfully,
            (await shipment.GetResponseAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(30), cancellationToken)).Status);
        var message = await _email.Started.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        Assert.Equal((customer, "Your order shipped"), (message.Recipient, message.Subject));
        Assert.False(message.CancellationToken.IsCancellationRequested);
        var notification = grain.GetDurableTask<object>($"notify-{customer}");
        Assert.Equal(DurableTaskStatus.Pending, await notification.GetStatusAsync(cancellationToken: cancellationToken));

        _email.Complete.TrySetResult();
        Assert.Equal(DurableTaskStatus.CompletedSuccessfully,
            (await notification.GetResponseAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(30), cancellationToken)).Status);
    }

    private sealed class EmailSender : IEmailSender
    {
        public TaskCompletionSource<(string Recipient, string Subject, CancellationToken CancellationToken)> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Complete { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task SendAsync(string recipient, string subject, CancellationToken cancellationToken)
        {
            Started.TrySetResult((recipient, subject, cancellationToken));
            await Complete.Task.WaitAsync(cancellationToken);
        }
    }
}
