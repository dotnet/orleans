namespace UnitTests.GrainInterfaces
{
    /// <summary>
    /// A grain which returns IAsyncEnumerable
    /// </summary>
    public interface IObservableGrain : IGrainWithGuidKey
    {
        ValueTask Complete();
        ValueTask Fail();
        ValueTask Deactivate();
        ValueTask OnNext(string data);
        IAsyncEnumerable<string> GetValues();

        ValueTask<List<(string InterfaceName, string MethodName)>> GetIncomingCalls();
    }
}
