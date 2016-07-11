using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Orleans.Providers;

namespace UnitTests.SqlStatisticsPublisherTests
{
    internal class StatisticsPublisherProviderConfig : IProviderConfiguration
    {
        private readonly ReadOnlyDictionary<string, string> props;

        public StatisticsPublisherProviderConfig(string adoInvariant, string connectionString)
        {
            props = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>
            {
                {"AdoInvariant", adoInvariant},
                {"ConnectionString", connectionString}
            });
        }

        public string Type
        {
            get { throw new NotImplementedException(); }
        }

        public string Name
        {
            get { throw new NotImplementedException(); }
        }

        public ReadOnlyDictionary<string, string> Properties
        {
            get { return props; }
        }

        public IList<IProvider> Children
        {
            get { throw new NotImplementedException(); }
        }

        public void SetProperty(string key, string val)
        {
            throw new NotImplementedException();
        }

        public bool RemoveProperty(string key)
        {
            throw new NotImplementedException();
        }
    }
}
