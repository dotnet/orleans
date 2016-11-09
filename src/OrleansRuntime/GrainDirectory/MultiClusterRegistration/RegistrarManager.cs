using System;
using System.Collections.Generic;
using Orleans.GrainDirectory;
using Orleans.Runtime.Configuration;

namespace Orleans.Runtime.GrainDirectory
{
    /// <summary>
    /// Maps multi-cluster registration strategies to the corresponding registrar
    /// </summary>
    internal class RegistrarManager
    {
        private readonly Dictionary<Type, IGrainRegistrar> registrars = new Dictionary<Type, IGrainRegistrar>();
        
        public RegistrarManager(GrainDirectoryPartition directoryPartition, GlobalSingleInstanceActivationMaintainer gsiActivationMaintainer, GlobalConfiguration globalConfig, Logger logger)
        {
            this.Register<ClusterLocalRegistration>(new ClusterLocalRegistrar(directoryPartition));
            this.Register<GlobalSingleInstanceRegistration>(
                new GlobalSingleInstanceRegistrar(
                    directoryPartition,
                    logger,
                    gsiActivationMaintainer,
                    globalConfig.GlobalSingleInstanceNumberRetries));
        }

        private void Register<TStrategy>(IGrainRegistrar directory)
            where TStrategy : MultiClusterRegistrationStrategy
        {
            this.registrars.Add(typeof(TStrategy), directory);
        }

        public IGrainRegistrar GetRegistrarForGrain(GrainId grainId)
        {
            MultiClusterRegistrationStrategy strategy;

            var typeCode = grainId.GetTypeCode();

            if (typeCode != 0)
            {
                string unusedGrainClass;
                PlacementStrategy unusedPlacement;
                GrainTypeManager.Instance.GetTypeInfo(grainId.GetTypeCode(), out unusedGrainClass, out unusedPlacement, out strategy);
            }
            else
            {
                // special case for Membership grain or client grain.
                strategy = ClusterLocalRegistration.Singleton; // default
            }

            return this.registrars[strategy.GetType()];
        }
    }
}
