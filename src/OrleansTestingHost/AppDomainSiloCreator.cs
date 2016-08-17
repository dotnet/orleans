namespace Orleans.TestingHost
{
    using System;

    using Orleans.Runtime;
    using Orleans.Runtime.Configuration;

    public class AppDomainSiloCreator : MarshalByRefObject
    {
        public AppDomainSiloCreator(string siloName, Silo.SiloType type, ClusterConfiguration config)
        {
            this.Silo = new Silo(siloName, type, config);
        }

        protected AppDomainSiloCreator() { }

        /// <summary>
        /// Gets the silo instance.
        /// </summary>
        public Silo Silo { get; protected set; }
    }
}