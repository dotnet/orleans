using System.Threading.Tasks;
using Orleans;

namespace TestGrains
{
    public interface ITestGenericGrain<T, U> : IGrainWithStringKey
    {
        Task<T> TestT(T value);

        Task<U> TestU(U value);

        Task<T> TestTU(T value1, U value2);
    }

    public class TestGenericGrain<T, U> : Grain, ITestGenericGrain<T, U>
    {
        public Task<T> TestT(T value)
        {
            return Task.FromResult(value);
        }

        public Task<T> TestTU(T value1, U value2)
        {
            return Task.FromResult(value1);
        }

        public Task<U> TestU(U value)
        {
            return Task.FromResult(value);
        }
    }
}