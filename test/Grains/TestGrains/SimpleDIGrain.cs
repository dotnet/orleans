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
            grainFactoryId = ObjectIdGenerator.GetId(this.injectedGrainFactory, out _);
            this.grainContextAccessor = grainContextAccessor;
        }

        public override Task OnActivateAsync(CancellationToken cancellationToken)
        {
            originalGrainContext = grainContextAccessor.GrainContext;
            return base.OnActivateAsync(cancellationToken);
        }

        public Task<long> GetLongValue() => injectedService.GetTicks();

        public Task<string> GetStringValue() => Task.FromResult(grainContextAccessor.GrainContext.GrainId.ToString());

        public Task<string> GetInjectedSingletonServiceValue() => Task.FromResult(injectedService.GetInstanceValue());

        public Task<string> GetInjectedScopedServiceValue() => Task.FromResult(injectedScopedService.GetInstanceValue());

        public Task<long> GetGrainFactoryId() => Task.FromResult(grainFactoryId);

        public Task DoDeactivate()
        {
            DeactivateOnIdle();
            return Task.CompletedTask;
        }

        public Task AssertCanResolveSameServiceInstances()
        {
            if (!ReferenceEquals(ServiceProvider.GetRequiredService<IInjectedService>(), injectedService)) throw new Exception("singleton not equal");
            if (!ReferenceEquals(ServiceProvider.GetRequiredService<IInjectedScopedService>(), injectedScopedService)) throw new Exception("scoped not equal");
            if (!ReferenceEquals(ServiceProvider.GetRequiredService<IGrainContextAccessor>().GrainContext, originalGrainContext)) throw new Exception("scoped grain activation context not equal");

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

        public Task<long> GetLongValue() => Task.FromResult((long)numberOfReleasedInstancesBeforeThisActivation);

        public Task<string> GetStringValue() => Task.FromResult(someValueThatIsNotRegistered);
        public Task DoDeactivate()
        {
            DeactivateOnIdle();
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

        public Task<long> GetTicks() => Task.FromResult(DateTime.UtcNow.Ticks);
        public string GetInstanceValue() => instanceValue;

        public InjectedService(ILoggerFactory loggerFactory)
        {
            logger = loggerFactory.CreateLogger<InjectedService>();
        }

        public void Dispose() => logger.LogInformation("Disposed instance {Value}", instanceValue);
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
            logger = loggerFactory.CreateLogger<InjectedScopedService>();
        }

        public void Dispose() => logger.LogInformation("Disposed instance {Value}", instanceValue);

        public string GetInstanceValue() =>  instanceValue;
    }
}
