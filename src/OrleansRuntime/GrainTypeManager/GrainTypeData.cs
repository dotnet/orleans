using System;
using System.Collections.Generic;
using Orleans.Concurrency;
using Orleans.GrainDirectory;
using Orleans.Placement;


namespace Orleans.Runtime
{
    /// <summary>
    /// Grain type meta data
    /// </summary>
    [Serializable]
    internal class GrainTypeData
    {
        internal Type Type { get; private set; }
        internal string GrainClass { get; private set; }
        internal List<Type> RemoteInterfaceTypes { get; private set; }
        internal Type StateObjectType { get; private set; }
        internal bool IsReentrant { get; private set; }
        internal bool IsStatelessWorker { get; private set; }
   
     
        public GrainTypeData(Type type, Type stateObjectType)
        {
            Type = type;
            IsReentrant = Type.GetCustomAttributes(typeof (ReentrantAttribute), true).Length > 0;
            IsStatelessWorker = Type.GetCustomAttributes(typeof(StatelessWorkerAttribute), true).Length > 0;
            GrainClass = TypeUtils.GetFullName(type);
            RemoteInterfaceTypes = GetRemoteInterfaces(type); ;
            StateObjectType = stateObjectType;
        }

        /// <summary>
        /// Returns a list of remote interfaces implemented by a grain class or a system target
        /// </summary>
        /// <param name="grainType">Grain or system target class</param>
        /// <returns>List of remote interfaces implemented by grainType</returns>
        private static List<Type> GetRemoteInterfaces(Type grainType)
        {
            var interfaceTypes = new List<Type>();

            while (grainType != typeof(Grain) && grainType != typeof(Object))
            {
                foreach (var t in grainType.GetInterfaces())
                {
                    if (t == typeof(IAddressable)) continue;

                    if (CodeGeneration.GrainInterfaceUtils.IsGrainInterface(t) && !interfaceTypes.Contains(t))
                        interfaceTypes.Add(t);
                }

                // Traverse the class hierarchy
                grainType = grainType.BaseType;
            }

            return interfaceTypes;
        }

        private static bool GetPlacementStrategy<T>(
            Type grainInterface, Func<T, PlacementStrategy> extract, out PlacementStrategy placement)
                where T : class
        {
            var attribs = grainInterface.GetCustomAttributes(typeof(T), inherit: true);
            switch (attribs.Length)
            {
                case 0:
                    placement = null;
                    return false;

                case 1:
                    placement = extract((T)attribs[0]);
                    return placement != null;

                default:
                    throw new InvalidOperationException(
                        string.Format(
                            "More than one {0} cannot be specified for grain interface {1}",
                            typeof(T).Name,
                            grainInterface.Name));
            }
        }

#pragma warning disable 612,618
        internal static PlacementStrategy GetPlacementStrategy(Type grainClass)
        {
            PlacementStrategy placement;

            if (GetPlacementStrategy<StatelessWorkerAttribute>(
                grainClass,
                (StatelessWorkerAttribute attr) =>
                {
                    return new StatelessWorkerPlacement(attr.MaxLocalWorkers);
                },
                out placement))
            {
                return placement;
            }

            if (GetPlacementStrategy<PlacementAttribute>(
                grainClass,
                a => a.PlacementStrategy,
                out placement))
            {
                return placement;
            }

            return PlacementStrategy.GetDefault();
        }

        internal static MultiClusterRegistrationStrategy GetMultiClusterRegistrationStrategy(Type grainClass)
        {
            var attribs = grainClass.GetCustomAttributes(typeof(Orleans.MultiCluster.RegistrationAttribute), inherit: true);

            switch (attribs.Length)
            {
                case 0:
                    return ClusterLocalRegistration.Singleton;
                case 1:
                    return ((Orleans.MultiCluster.RegistrationAttribute)attribs[0]).RegistrationStrategy;
                default:
                    throw new InvalidOperationException(
                        string.Format(
                            "More than one {0} cannot be specified for grain interface {1}",
                            typeof(MultiClusterRegistrationStrategy).Name,
                            grainClass.Name));
            }
        }
    }
}
