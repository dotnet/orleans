using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

using Orleans.CodeGeneration;
using Orleans.Core;
using Orleans.Concurrency;
using Orleans.GrainDirectory;
using Orleans.MultiCluster;
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
        internal Func<InvokeMethodRequest, bool> MayInterleave { get; private set; }
        internal MultiClusterRegistrationStrategy MultiClusterRegistrationStrategy { get; private set; }
   
        public GrainTypeData(Type type, Type stateObjectType)
        {
            var typeInfo = type.GetTypeInfo();
            Type = type;
            IsReentrant = typeInfo.GetCustomAttributes(typeof (ReentrantAttribute), true).Any();
            // TODO: shouldn't this use GrainInterfaceUtils.IsStatelessWorker?
            IsStatelessWorker = typeInfo.GetCustomAttributes(typeof(StatelessWorkerAttribute), true).Any();
            GrainClass = TypeUtils.GetFullName(typeInfo);
            RemoteInterfaceTypes = GetRemoteInterfaces(type); ;
            StateObjectType = stateObjectType;
            MayInterleave = GetMayInterleavePredicate(typeInfo) ?? (_ => false);
            MultiClusterRegistrationStrategy = MultiClusterRegistrationStrategy.FromAttributes(typeInfo.GetCustomAttributes(typeof(RegistrationAttribute), true));
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
                grainType = grainType.GetTypeInfo().BaseType;
            }

            return interfaceTypes;
        }

        private static bool GetPlacementStrategy<T>(
            Type grainInterface, Func<T, PlacementStrategy> extract, out PlacementStrategy placement)
                where T : Attribute
        {
            var attribs = grainInterface.GetTypeInfo().GetCustomAttributes<T>(inherit: true).ToArray();
            switch (attribs.Length)
            {
                case 0:
                    placement = null;
                    return false;

                case 1:
                    placement = extract(attribs[0]);
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
        internal static PlacementStrategy GetPlacementStrategy(Type grainClass, PlacementStrategy defaultPlacement)
        {
            PlacementStrategy placement;

            if (GetPlacementStrategy<StatelessWorkerAttribute>(
                grainClass,
                attr => new StatelessWorkerPlacement(attr.MaxLocalWorkers),
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

            return defaultPlacement;
        }

        internal static MultiClusterRegistrationStrategy GetMultiClusterRegistrationStrategy(Type grainClass)
        {
            var attribs = grainClass.GetTypeInfo().GetCustomAttributes<Orleans.MultiCluster.RegistrationAttribute>(inherit: true).ToArray();

            switch (attribs.Length)
            {
                case 0:
                    return MultiClusterRegistrationStrategy.GetDefault(); // no strategy is specified
                case 1:
                    return attribs[0].RegistrationStrategy;
                default:
                    throw new InvalidOperationException(
                        string.Format(
                            "More than one {0} cannot be specified for grain interface {1}",
                            typeof(MultiClusterRegistrationStrategy).Name,
                            grainClass.Name));
            }
        }

        /// <summary>
        /// Returns interleave predicate depending on whether class is marked with <see cref="MayInterleaveAttribute"/> or not.
        /// </summary>
        /// <param name="grainType">Grain class.</param>
        /// <returns></returns>
        private static Func<InvokeMethodRequest, bool> GetMayInterleavePredicate(TypeInfo grainType)
        {
            if (!grainType.GetCustomAttributes<MayInterleaveAttribute>().Any())
                return null;

            if (grainType.GetCustomAttributes(typeof(ReentrantAttribute), true).Any())
                throw new InvalidOperationException(
                    $"Class {grainType.FullName} is already marked with Reentrant attribute");

            var callbackMethodName = grainType.GetCustomAttribute<MayInterleaveAttribute>().CallbackMethodName;
            var method = grainType.GetMethod(callbackMethodName, BindingFlags.Public | BindingFlags.Static);
            if (method == null)
                throw new InvalidOperationException(
                    $"Class {grainType.FullName} doesn't declare public static method " +
                    $"with name {callbackMethodName} specified in MayInterleave attribute");

            if (method.ReturnType != typeof(bool) || 
                method.GetParameters().Length != 1 || 
                method.GetParameters()[0].ParameterType != typeof(InvokeMethodRequest))
                throw new InvalidOperationException(
                    $"Wrong signature of callback method {callbackMethodName} " +
                    $"specified in MayInterleave attribute for grain class {grainType.FullName}. \n" +
                    $"Expected: public static bool {callbackMethodName}(InvokeMethodRequest req)");

            var parameter = Expression.Parameter(typeof(InvokeMethodRequest));
            var call = Expression.Call(null, method, parameter);
            var predicate = Expression.Lambda<Func<InvokeMethodRequest, bool>>(call, parameter).Compile();

            return predicate;
        }
    }
}
