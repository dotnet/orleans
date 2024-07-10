namespace DistributedTests.GrainInterfaces;

public interface ITreeGrain : IGrainWithIntegerCompoundKey
{
    public ValueTask Ping();
}

