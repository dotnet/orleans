// ReSharper disable once CheckNamespace
// A grain interface which include an "Orleans" part in the namespace to ensure that code generation uses fully
// qualified names.
namespace TestGrainInterfaces.Orleans
{
    using System.Threading.Tasks;

    using global::Orleans;

    public interface ITrickyNamespaceGrain : IGrain
    {
        Task<NoNamespace> Do(NoNamespace notEventOneNamespace);
    }
}

 /// <summary>
 /// Class with no namespace.
 /// </summary>
public class NoNamespace
{
}