namespace BenchmarkGrainInterfaces.Ping;

public interface ITreeGrain : IGrainWithIntegerCompoundKey
{
    public ValueTask Ping();
}

