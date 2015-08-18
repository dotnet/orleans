using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.MembershipService;
using Orleans.Runtime.ReminderService;
using Orleans.Streams;

namespace Orleans.Providers.DependencyInjection
{
    public abstract class DependencyResolverProviderBase : IDependencyResolverProvider
    {
        public string Name
        {
            get { return "Orleans DependencyResolver Provider"; }
        }

        public System.Threading.Tasks.Task Init(string name, IProviderRuntime providerRuntime, IProviderConfiguration config)
        {
            //
            // This method will not be called by the DependenceResolverProviderManager, when loading the provider.
            //

            throw new System.NotSupportedException();
        }

        public abstract IDependencyResolver ConfigureResolver(params System.Type[] systemTypesToRegister);

        public IDependencyResolver GetDependencyResolver(ClusterConfiguration config, NodeConfiguration nodeConfig, TraceLogger logger)
        {
            var dependencyResolver = ConfigureResolver(
                typeof(GrainBasedMembershipTable),
                typeof(GrainBasedReminderTable),
                typeof(GrainBasedPubSubRuntime));

            return dependencyResolver;
        }
    }
}
