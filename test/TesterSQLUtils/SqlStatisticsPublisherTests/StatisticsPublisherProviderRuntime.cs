using System;
using System.Threading.Tasks;
using Orleans;
using Orleans.Providers;
using Orleans.Runtime;

namespace UnitTests.SqlStatisticsPublisherTests
{
    internal class StatisticsPublisherProviderRuntime : IProviderRuntime
    {
        private readonly Logger logger;
        private InvokeInterceptor invokeInterceptor;

        public StatisticsPublisherProviderRuntime(Logger logger)
        {
            this.logger = logger;
        }

        public Logger GetLogger(string loggerName)
        {
            return logger;
        }

        public Guid ServiceId
        {
            get { throw new NotImplementedException(); }
        }

        public string SiloIdentity
        {
            get { throw new NotImplementedException(); }
        }

        public IGrainFactory GrainFactory
        {
            get { throw new NotImplementedException(); }
        }

        public IServiceProvider ServiceProvider
        {
            get { throw new NotImplementedException(); }
        }

        public void SetInvokeInterceptor(InvokeInterceptor interceptor)
        {
            this.invokeInterceptor = interceptor;
        }

        public InvokeInterceptor GetInvokeInterceptor()
        {
            return this.invokeInterceptor;
        }

        public Task<Tuple<TExtension, TExtensionInterface>> BindExtension<TExtension, TExtensionInterface>(Func<TExtension> newExtensionFunc)
            where TExtension : IGrainExtension
            where TExtensionInterface : IGrainExtension
        {
            throw new NotImplementedException();
        }
    }
}
