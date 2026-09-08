namespace BenchmarkGrainInterfaces.Ping;

public interface IPayloadGrain : IGrainWithIntegerKey
{
    ValueTask Run(byte[] payload);
}
