namespace UnitTests.GrainInterfaces
{
    public interface IIdleActivationGcTestGrain1 : IGrainWithGuidKey
    {
        Task Nop();
    }

    public interface IIdleActivationGcTestGrain2 : IGrainWithGuidKey
    {
        Task Nop();
    }

    public interface IBusyActivationGcTestGrain1 : IGrainWithGuidKey
    {
        Task Nop();
        Task Delay(TimeSpan dt);
        Task<string> IdentifyActivation();
        
        /// <summary>
        /// Blocks the activation until released via static method.
        /// This keeps the activation truly "busy" (not inactive) for testing.
        /// </summary>
        Task BlockUntilReleased();
    }

    public interface IBusyActivationGcTestGrain2 : IGrainWithGuidKey
    {
        Task Nop();
    }

    public interface ICollectionSpecificAgeLimitForTenSecondsActivationGcTestGrain : IGrainWithGuidKey
    {
        Task Nop();
    }

    public interface ICollectionSpecificAgeLimitForZeroSecondsActivationGcTestGrain : IGrainWithGuidKey
    {
        Task Nop();
    }

    public interface IStatelessWorkerActivationCollectorTestGrain1 : IGrainWithGuidKey
    {
        Task Nop();
        Task Delay(TimeSpan dt);
        Task<string> IdentifyActivation();
        
        /// <summary>
        /// Blocks the activation until <see cref="ReleaseBlock"/> is called.
        /// This keeps the activation truly "busy" (not inactive) for testing.
        /// </summary>
        Task BlockUntilReleased();
        
        /// <summary>
        /// Releases a blocked activation.
        /// </summary>
        Task ReleaseBlock();
    }
}
