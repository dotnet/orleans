namespace UnitTests.GrainInterfaces
{
    public interface IChainedGrain : IGrainWithIntegerKey
    {
        Task<int> GetId();
        Task<int> GetX();
        Task<IChainedGrain> GetNext();
        //[ReadOnly]
        Task<int> GetCalculatedValue();
        Task SetNext(IChainedGrain next);
        Task SetNextNested(ChainGrainHolder next);
        //[ReadOnly]
        Task Validate(bool nextIsSet);
        Task PassThis(IChainedGrain next);
        Task PassNull(IChainedGrain next);
        Task PassThisNested(ChainGrainHolder next);
        Task PassNullNested(ChainGrainHolder next);
    }
    
    [GenerateSerializer]
    public class ChainGrainHolder
    {
        [Id(0)]
        public IChainedGrain Next { get; set; }
    }
}
