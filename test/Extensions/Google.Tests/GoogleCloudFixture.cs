using TestExtensions;

namespace Orleans.Tests.Google;

public class GoogleCloudFixture : TestEnvironmentFixture, IAsyncLifetime
{
    public GoogleEmulatorHost Emulator { get; }

    public GoogleCloudFixture() : base()
    {
        this.Emulator = new();
    }

    public Task DisposeAsync() => this.Emulator.DisposeAsync().AsTask();
    public Task InitializeAsync() => this.Emulator.Initialize();
}