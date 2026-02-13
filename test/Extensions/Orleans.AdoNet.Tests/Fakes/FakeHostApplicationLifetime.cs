using Microsoft.Extensions.Hosting;

namespace Tester.AdoNet.Fakes;

/// <summary>
/// A fake implementation of <see cref="IHostApplicationLifetime"/> for unit test use.
/// </summary>
internal sealed class FakeHostApplicationLifetime : IHostApplicationLifetime
{
    private readonly CancellationTokenSource _applicationStarted = new();
    private readonly CancellationTokenSource _applicationStopping = new();
    private readonly CancellationTokenSource _applicationStopped = new();

    public CancellationToken ApplicationStarted => _applicationStarted.Token;

    public CancellationToken ApplicationStopping => _applicationStopping.Token;

    public CancellationToken ApplicationStopped => _applicationStopped.Token;

    public void StartApplication() => _applicationStarted.Cancel();

    public void StopApplication()
    {
        _applicationStopping.Cancel();
        _applicationStopped.Cancel();
    }

    public void Dispose()
    {
        _applicationStarted.Dispose();
        _applicationStopping.Dispose();
        _applicationStopped.Dispose();
    }
}