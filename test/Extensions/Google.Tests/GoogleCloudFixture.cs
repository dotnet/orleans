namespace Google.Tests;

public class GoogleCloudFixture : IAsyncLifetime
{
    public GoogleEmulatorHost Emulator { get; }

    public GoogleCloudFixture()
    {
        this.Emulator = new();
    }

    public Task DisposeAsync() => this.Emulator.DisposeAsync().AsTask();
    public Task InitializeAsync() => this.Emulator.Initialize();
}