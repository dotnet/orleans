using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Runtime;
using UnitTests.GrainInterfaces;


namespace UnitTests.Grains
{
    /// <summary>
    /// A simple grain that allows to set two arguments and then multiply them.
    /// </summary>
    public class SimpleGrain : Grain, ISimpleGrain 
    {
        public const string SimpleGrainNamePrefix = "UnitTests.Grains.SimpleG";

        protected ILogger logger;

        public SimpleGrain(ILoggerFactory loggerFactory)
        {
            this.logger = loggerFactory.CreateLogger($"{this.GetType().Name}-{this.IdentityString}");
        }

        protected int A { get; set; }
        protected int B { get; set; }

        public override Task OnActivateAsync(CancellationToken cancellationToken)
        {
            logger.LogInformation("Activate.");
            return Task.CompletedTask;
        }

        public Task SetA(int a)
        {
            logger.LogInformation("SetA={A}", a);
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

        public Task<int> GetAxB() => Task.FromResult(A * B);

        public Task<int> GetAxB(int a, int b) => Task.FromResult(a * b);

        public Task<int> GetA() => Task.FromResult(A);

        public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
        {
            logger.LogInformation("OnDeactivateAsync.");
            return Task.CompletedTask;
        }
    }
}
