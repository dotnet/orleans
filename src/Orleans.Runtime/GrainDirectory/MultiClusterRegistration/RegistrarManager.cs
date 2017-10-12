using System;
using Orleans.GrainDirectory;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Runtime.GrainDirectory
{
    /// <summary>
    /// Maps multi-cluster registration strategies to the corresponding registrar
    /// </summary>
    internal class RegistrarManager
    {
        private readonly GrainTypeManager grainTypeManager;
        private readonly object registrarLock = new object();
        private readonly IServiceProvider serviceProvider;

        private IReadOnlyDictionary<Type, IGrainRegistrar> registrars = new Dictionary<Type, IGrainRegistrar>();

        public RegistrarManager(IServiceProvider serviceProvider, GrainTypeManager grainTypeManager)
        {
            this.grainTypeManager = grainTypeManager;
            this.serviceProvider = serviceProvider;
        }

        public IGrainRegistrar GetRegistrarForGrain(GrainId grainId)
        {
            MultiClusterRegistrationStrategy strategy;

            var typeCode = grainId.TypeCode;

            if (typeCode != 0)
            {
                string unusedGrainClass;
                PlacementStrategy unusedPlacement;
                this.grainTypeManager.GetTypeInfo(grainId.TypeCode, out unusedGrainClass, out unusedPlacement, out strategy);
            }
            else
            {
                // special case for Membership grain or client grain.
                strategy = ClusterLocalRegistration.Singleton; // default
            }

            return this.GetRegistrar(strategy);
        }

        private IGrainRegistrar GetRegistrar(IMultiClusterRegistrationStrategy strategy)
        {
            IGrainRegistrar result;
            var strategyType = strategy.GetType();
            if (!this.registrars.TryGetValue(strategyType, out result))
            {
                result = this.AddRegistrar(strategyType);
            }

            return result;
        }

        private IGrainRegistrar AddRegistrar(Type strategyType)
        {
            IGrainRegistrar registrar;
            lock (this.registrarLock)
            {
                if (!this.registrars.TryGetValue(strategyType, out registrar))
                {
                    var directorType = typeof(IGrainRegistrar<>).MakeGenericType(strategyType);
                    registrar = (IGrainRegistrar) this.serviceProvider.GetRequiredService(directorType);
                    var newRegistrars = new Dictionary<Type, IGrainRegistrar>((Dictionary<Type, IGrainRegistrar>)this.registrars)
                    {
                        [strategyType] = registrar
                    };

                    this.registrars = newRegistrars;
                }
            }

            return registrar;
        }
    }
}