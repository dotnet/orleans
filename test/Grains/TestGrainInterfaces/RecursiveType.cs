using System;
using System.Threading.Tasks;
using Orleans;

namespace UnitTests.GrainInterfaces
{
    public interface IReferenceRecursiveTypeGrain : IGrainWithGuidKey
    {
        Task<RecursiveType> Echo(RecursiveType arg);
    }

    /// <summary>
    /// These classes form a repro for https://github.com/dotnet/orleans/issues/5473, which resulted in a
    /// StackOverflowException during code generation.
    /// </summary>
    [Serializable]
    public class RecursiveType : SelfTyped<RecursiveType>
    {
    }

    public abstract class SelfTyped<T> where T : SelfTyped<T>
    {
    }
}
