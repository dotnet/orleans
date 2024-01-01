namespace Orleans.TestingHost.Tests.Grains
{
    public interface ISimpleGrain : IGrainWithIntegerKey
    {
        Task SetA(int a);
        Task SetB(int b);
        Task IncrementA();
        Task<int> GetAxB();
        Task<int> GetAxB(int a, int b);
        Task<int> GetA();
    }

    /// <summary>
    /// A simple grain that allows to set two arguments and then multiply them.
    /// </summary>
    public class SimpleGrain : Grain, ISimpleGrain
    {
        protected int A { get; set; }
        protected int B { get; set; }

        public Task SetA(int a)
        {
            A = a;
            return Task.CompletedTask;
        }

        public Task SetB(int b)
        {
            this.B = b;
            return Task.CompletedTask;
        }

        public Task IncrementA()
        {
            A = A + 1;
            return Task.CompletedTask;
        }

        public Task<int> GetAxB()
        {
            return Task.FromResult(A * B);
        }

        public Task<int> GetAxB(int a, int b)
        {
            return Task.FromResult(a * b);
        }

        public Task<int> GetA()
        {
            return Task.FromResult(A);
        }
    }
}
