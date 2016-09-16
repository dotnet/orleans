using System;
using System.Collections.Generic;
using Orleans.GrainDirectory;

namespace Orleans.Runtime.GrainDirectory
{
    /// <summary>
    /// Maps multi-cluster registration strategies to the corresponding registrar
    /// </summary>
    internal class RegistrarManager
    {
        private readonly Dictionary<Type, IGrainRegistrar> registrars = new Dictionary<Type, IGrainRegistrar>();

        public static RegistrarManager Instance { get; private set; }


        private RegistrarManager()
        {
        }

        public static void InitializeGrainDirectoryManager(LocalGrainDirectory router, int numRetriesForGSI)
        {
            Instance = new RegistrarManager();
            Instance.Register<ClusterLocalRegistration>(new ClusterLocalRegistrar(router.DirectoryPartition));
            Instance.Register<GlobalSingleInstanceRegistration>(new GlobalSingleInstanceRegistrar(router.DirectoryPartition, router.Logger, router.GsiActivationMaintainer, numRetriesForGSI));
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
