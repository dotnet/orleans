using System.Threading.Tasks;

namespace Orleans.Streams
{
    internal interface IInternalAsyncBatchObserver<in T> : IAsyncBatchObserver<T>
    {
        Task Cleanup();
    }
}