namespace UnitTests.GrainInterfaces

{
    public interface IMultifacetWriter : IGrainWithIntegerKey
    {
        Task SetValue(int x);
        //event ValueUpdateEventHandler ValueReadEvent;
        //event ValueUpdateEventHandler CommonEvent;
    }
}
