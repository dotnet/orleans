using System.Runtime.Serialization;
using UnitTests.GrainInterfaces;
using Orleans.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace UnitTests.Grains
{
    public class DIGrainWithInjectedServices : Grain, IDIGrainWithInjectedServices
    {
        private readonly IInjectedService injectedService;
        private readonly IInjectedScopedService injectedScopedService;
        private readonly IGrainFactory injectedGrainFactory;
        private readonly long grainFactoryId;
        public static readonly ObjectIDGenerator ObjectIdGenerator = new ObjectIDGenerator();
        private readonly IGrainContextAccessor grainContextAccessor;
        private IGrainContext originalGrainContext;

        public DIGrainWithInjectedServices(IInjectedService injectedService, IInjectedScopedService injectedScopedService,  IGrainFactory injectedGrainFactory, IGrainContextAccessor grainContextAccessor)
        {
            this.injectedService = injectedService;
            this.injectedGrainFactory = injectedGrainFactory;
            this.injectedScopedService = injectedScopedService;
            // get the object Id for injected GrainFactory, 
            // object Id will be the same if the underlying object is the same,
            // this is one way to prove that this GrainFactory is injected from DI
            this.grainFactoryId = ObjectIdGenerator.GetId(this.injectedGrainFactory, out _);
            this.grainContextAccessor = grainContextAccessor;
        }

        public override Task OnActivateAsync(CancellationToken cancellationToken)
        {
            this.originalGrainContext = this.grainContextAccessor.GrainContext;
            return base.OnActivateAsync(cancellationToken);
        }

        public Task<long> GetLongValue()
        {
            return injectedService.GetTicks();
        }

        public Task<string> GetStringValue()
        {
            return Task.FromResult(this.grainContextAccessor.GrainContext.GrainId.ToString());
        }

        public Task<string> GetInjectedSingletonServiceValue()
        {
            return Task.FromResult(this.injectedService.GetInstanceValue());
        }

        public Task<string> GetInjectedScopedServiceValue()
        {
            return Task.FromResult(this.injectedScopedService.GetInstanceValue());
        }

        public Task<long> GetGrainFactoryId()
        {
            return Task.FromResult(this.grainFactoryId);
        }

        public Task DoDeactivate()
        {
            this.DeactivateOnIdle();
            return Task.CompletedTask;
        }

        public Task AssertCanResolveSameServiceInstances()
        {
            if (!ReferenceEquals(this.ServiceProvider.GetRequiredService<IInjectedService>(), this.injectedService)) throw new Exception("singleton not equal");
            if (!ReferenceEquals(this.ServiceProvider.GetRequiredService<IInjectedScopedService>(), this.injectedScopedService)) throw new Exception("scoped not equal");
            if (!ReferenceEquals(this.ServiceProvider.GetRequiredService<IGrainContextAccessor>().GrainContext, this.originalGrainContext)) throw new Exception("scoped grain activation context not equal");

            return Task.CompletedTask;
        }
    }

    [GrainType("explicitly-registered")]
    public class ExplicitlyRegisteredSimpleDIGrain : Grain, ISimpleDIGrain
    {
        private readonly IInjectedService injectedService;
        private readonly string someValueThatIsNotRegistered;
        private readonly int numberOfReleasedInstancesBeforeThisActivation;

        public ExplicitlyRegisteredSimpleDIGrain(IInjectedService injectedService, string someValueThatIsNotRegistered, int numberOfReleasedInstancesBeforeThisActivation)
        {
            this.injectedService = injectedService;
            this.someValueThatIsNotRegistered = someValueThatIsNotRegistered;
            this.numberOfReleasedInstancesBeforeThisActivation = numberOfReleasedInstancesBeforeThisActivation;
        }

        public Task<long> GetLongValue()
        {
            return Task.FromResult((long)numberOfReleasedInstancesBeforeThisActivation);
        }

        public Task<string> GetStringValue()
        {
            return Task.FromResult(this.someValueThatIsNotRegistered);
        }
        public Task DoDeactivate()
        {
            this.DeactivateOnIdle();
            return Task.CompletedTask;
        }
    }

    public interface IInjectedService
    {
        Task<long> GetTicks();
        string GetInstanceValue();
    }

    public class InjectedService : IInjectedService, IDisposable
    {
        private readonly string instanceValue = Guid.NewGuid().ToString();
        private readonly ILogger logger;

        public Task<long> GetTicks()
        {
            return Task.FromResult(DateTime.UtcNow.Ticks);
        }
        public string GetInstanceValue() => this.instanceValue;

        public InjectedService(ILoggerFactory loggerFactory)
        {
            this.logger = loggerFactory.CreateLogger<InjectedService>();
        }

        public void Dispose()
        {
            logger.LogInformation("Disposed instance {Value}", this.instanceValue);
        }
    }

    public interface IInjectedScopedService
    {
        string GetInstanceValue();
    }

    public class InjectedScopedService : IInjectedScopedService, IDisposable
    {
        private readonly string instanceValue = Guid.NewGuid().ToString();
        private readonly ILogger logger;

        public InjectedScopedService(ILoggerFactory loggerFactory)
        {
            this.logger = loggerFactory.CreateLogger<InjectedScopedService>();
        }

        public void Dispose()
        {
            logger.LogInformation("Disposed instance {Value}", this.instanceValue);
        }

        public string GetInstanceValue() =>  this.instanceValue;
    }
}
