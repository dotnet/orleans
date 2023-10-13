namespace UnitTests.GrainInterfaces
{
    public interface ILivenessTestGrain : IGrainWithIntegerKey
    {
        // separate label that can be set
        Task<string> GetLabel();

        Task SetLabel(string label);

        Task<string> GetRuntimeInstanceId();

        Task<string> GetUniqueId();

        Task<ILivenessTestGrain> GetGrainReference();

        Task StartTimer();

    }
}
