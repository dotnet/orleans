using Orleans.Configuration;
using Xunit;

namespace Consul.Tests
{
    public class ConsulClusteringOptionsTests
    {
        [Fact, TestCategory("BVT"), TestCategory("Consul")]
        public void DefaultCreationBehaviorIsRetained()
        {            
            var options = new ConsulClusteringOptions();

            // ensure we set a default value.
            var actual = options.CreateClient;

            Assert.NotNull(actual);
        }       

        [Fact, TestCategory("BVT"), TestCategory("Consul")]
        public void ThrowsArgumentNullExceptionIfCallbackIsNull()
        {            
            var  options = new ConsulClusteringOptions();
            Func<IConsulClient> callback = null;

            // ensure we check the callback.
            void shouldThrow() => options.ConfigureConsulClient(callback);

            Assert.Throws<ArgumentNullException>(shouldThrow);
        }

        [Fact, TestCategory("BVT"), TestCategory("Consul")]
        public void WeCanInjectAConsulClient()
        {
            var fakeConsul = new FakeConsul();
            var options = new ConsulClusteringOptions();
            IConsulClient callback() => fakeConsul;

            //we can inject the consul
            options.ConfigureConsulClient(callback);

            var actual = options.CreateClient();
            Assert.Equal(fakeConsul, actual);
        }

        [Fact, TestCategory("BVT"), TestCategory("Consul")]
        public void WeCanUseConfigureToSetupTheDefaultClient()
        {
            var address = new Uri("http://localhost:8501");
            var token = "SomeToken";

            var options = new ConsulClusteringOptions();            

            //we can configure the default consult client
            options.ConfigureConsulClient(address, token);

            var client = (ConsulClient) options.CreateClient();

            Assert.Equal(address, client.Config.Address);
            Assert.Equal(token, client.Config.Token);
        }

        [Fact, TestCategory("BVT"), TestCategory("Consul")]
        public void WeCanUseConfigureToSetupTheDefaultClientWithoutAAclToken()
        {
            var address = new Uri("http://localhost:8501");           
            var options = new ConsulClusteringOptions();

            //we can configure the default consult client
            options.ConfigureConsulClient(address);

            var client = (ConsulClient)options.CreateClient();

            Assert.Equal(address, client.Config.Address);
            Assert.Null(client.Config.Token);
        }

        /// <summary>
        /// Fake Client with no function.
        /// </summary>
        private class FakeConsul : IConsulClient
        {
            [Obsolete]
            public IACLEndpoint ACL => throw new NotImplementedException();

            public IPolicyEndpoint Policy => throw new NotImplementedException();

            public IRoleEndpoint Role => throw new NotImplementedException();

            public ITokenEndpoint Token => throw new NotImplementedException();

            public IAgentEndpoint Agent => throw new NotImplementedException();

            public ICatalogEndpoint Catalog => throw new NotImplementedException();

            public IEventEndpoint Event => throw new NotImplementedException();

            public IHealthEndpoint Health => throw new NotImplementedException();

            public IKVEndpoint KV => throw new NotImplementedException();

            public IRawEndpoint Raw => throw new NotImplementedException();

            public ISessionEndpoint Session => throw new NotImplementedException();

            public IStatusEndpoint Status => throw new NotImplementedException();

            public IOperatorEndpoint Operator => throw new NotImplementedException();

            public IPreparedQueryEndpoint PreparedQuery => throw new NotImplementedException();

            public ICoordinateEndpoint Coordinate => throw new NotImplementedException();

            public ISnapshotEndpoint Snapshot => throw new NotImplementedException();

            public Task<IDistributedLock> AcquireLock(LockOptions opts, CancellationToken ct = default) => throw new NotImplementedException();
            public Task<IDistributedLock> AcquireLock(string key, CancellationToken ct = default) => throw new NotImplementedException();
            public Task<IDistributedSemaphore> AcquireSemaphore(SemaphoreOptions opts, CancellationToken ct = default) => throw new NotImplementedException();
            public Task<IDistributedSemaphore> AcquireSemaphore(string prefix, int limit, CancellationToken ct = default) => throw new NotImplementedException();
            public IDistributedLock CreateLock(LockOptions opts) => throw new NotImplementedException();
            public IDistributedLock CreateLock(string key) => throw new NotImplementedException();
            public void Dispose() => throw new NotImplementedException();
            public Task ExecuteInSemaphore(SemaphoreOptions opts, Action a, CancellationToken ct = default) => throw new NotImplementedException();
            public Task ExecuteInSemaphore(string prefix, int limit, Action a, CancellationToken ct = default) => throw new NotImplementedException();
            public Task ExecuteLocked(LockOptions opts, Action action, CancellationToken ct = default) => throw new NotImplementedException();
            public Task ExecuteLocked(LockOptions opts, CancellationToken ct, Action action) => throw new NotImplementedException();
            public Task ExecuteLocked(string key, Action action, CancellationToken ct = default) => throw new NotImplementedException();
            public Task ExecuteLocked(string key, CancellationToken ct, Action action) => throw new NotImplementedException();
            public IDistributedSemaphore Semaphore(SemaphoreOptions opts) => throw new NotImplementedException();
            public IDistributedSemaphore Semaphore(string prefix, int limit) => throw new NotImplementedException();
        }       
    }
}


