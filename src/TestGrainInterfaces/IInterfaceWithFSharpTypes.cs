using System.Threading.Tasks;

using Microsoft.FSharp.Core;
using Orleans;

namespace UnitTests.GrainInterfaces
{
    public interface IInterfaceWithFSharpTypes : IGrainWithGuidKey
    {
        Task<FSharpOption<int>> Echo(FSharpOption<int> value);
    }
}
