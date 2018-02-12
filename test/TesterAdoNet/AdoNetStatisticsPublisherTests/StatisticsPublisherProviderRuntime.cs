using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Providers;
using Orleans.Runtime;

namespace UnitTests.SqlStatisticsPublisherTests
{
    internal class StatisticsPublisherProviderRuntime : IProviderRuntime
    {
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

        public Task<Tuple<TExtension, TExtensionInterface>> BindExtension<TExtension, TExtensionInterface>(Func<TExtension> newExtensionFunc)
            where TExtension : IGrainExtension
            where TExtensionInterface : IGrainExtension
        {
            throw new NotImplementedException();
        }
    }
}
