namespace UnitTests.GrainInterfaces
{
    public interface IErrorGrain : ISimpleGrain
    {
        Task LogMessage(string msg);
        Task SetAError(int a);
        Task SetBError(int a);
        Task<int> GetAxBError();
        Task<int> GetAxBError(int a, int b);
        Task LongMethod(int waitTime);
        Task LongMethodWithError(int waitTime);
        Task DelayMethod(int milliseconds);
        Task Dispose();
        Task<int> UnobservedErrorImmediate();
        Task<int> UnobservedErrorDelayed();
        Task<int> UnobservedErrorContinuation2();
        Task<int> UnobservedErrorContinuation3();
        Task<int> UnobservedIgnoredError();
        Task AddChildren(List<IErrorGrain> children);
        Task<bool> ExecuteDelayed(TimeSpan delay);
    }
}
