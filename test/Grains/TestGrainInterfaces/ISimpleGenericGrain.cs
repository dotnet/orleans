namespace UnitTests.GrainInterfaces
{
    public interface ISimpleGenericGrain<T> : IGrainWithIntegerKey
    {
        Task Set(T t);

        Task Transform();

        Task<T> Get();

        Task CompareGrainReferences(ISimpleGenericGrain<T> clientRef);
    }
}
