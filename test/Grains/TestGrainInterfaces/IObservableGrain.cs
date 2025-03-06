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
        IAsyncEnumerable<int> GetValuesWithError(int errorIndex, bool waitAfterYield, string errorMessage);

        ValueTask<List<(string InterfaceName, string MethodName)>> GetIncomingCalls();
    }
}
