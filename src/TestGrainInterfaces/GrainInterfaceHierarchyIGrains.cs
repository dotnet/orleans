using System.Threading.Tasks;
using Orleans;

namespace TestGrainInterfaces
{
    public interface GrainInterfaceHierarchyIGrains
    {
        Task<string> DoIt();
    }

    public interface IDoSomethingWithMoreGrain : GrainInterfaceHierarchyIGrains, IGrainWithIntegerKey
    {
        Task<string> DoThat();
    }

    public interface IDoSomethingEmptyGrain : GrainInterfaceHierarchyIGrains, IGrainWithIntegerKey
    {
    }

    public interface IDoSomethingEmptyWithMoreGrain : IDoSomethingEmptyGrain
    {
        Task<string> DoMore();
    }

    public interface IDoSomethingWithMoreEmptyGrain : IDoSomethingEmptyWithMoreGrain
    {
    }

    public interface IDoSomethingCombinedGrain : IDoSomethingWithMoreGrain, IDoSomethingWithMoreEmptyGrain
    {
    }

}
