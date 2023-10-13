namespace UnitTests.GrainInterfaces
{
    // Note: Self-managed can only implement one grain interface, so have to use copy-paste rather than subclassing 

    public interface ISimpleActivateDeactivateTestGrain : IGrainWithIntegerKey
    {
        Task<string> DoSomething();
        Task DoDeactivate();
    }

    public interface ITailCallActivateDeactivateTestGrain : IGrainWithIntegerKey
    {
        Task<string> DoSomething();
        Task DoDeactivate();
    }

    public interface ILongRunningActivateDeactivateTestGrain : IGrainWithIntegerKey
    {
        Task<string> DoSomething();
        Task DoDeactivate();
    }

    public interface IBadActivateDeactivateTestGrain : IGrainWithIntegerKey
    {
        Task ThrowSomething();
        Task<long> GetKey();
    }

    public interface IBadConstructorTestGrain : IGrainWithIntegerKey
    {
        Task<string> DoSomething();
    }

    public interface ITaskActionActivateDeactivateTestGrain : IGrainWithIntegerKey
    {
        Task<string> DoSomething();
        Task DoDeactivate();
    }

    public interface ICreateGrainReferenceTestGrain : IGrainWithIntegerKey
    {
        Task<string> DoSomething();

        Task ForwardCall(IBadActivateDeactivateTestGrain otherGrain);
    }

    public interface IDeactivatingWhileActivatingTestGrain : IGrainWithIntegerKey
    {
        Task<string> DoSomething();
    }
    
}
