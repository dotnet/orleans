using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

using Orleans.CodeGeneration;
using Orleans.Concurrency;
using Orleans.GrainDirectory;
using Orleans.Placement;

namespace Orleans.Runtime
{
    /// <summary>
    /// Grain type meta data
    /// </summary>
    internal class GrainTypeData
    {
        internal Type Type { get; private set; }
        internal string GrainClass { get; private set; }
        internal List<Type> RemoteInterfaceTypes { get; private set; }
        internal bool IsReentrant { get; private set; }
        internal bool IsStatelessWorker { get; private set; }
        internal Func<InvokeMethodRequest, bool> MayInterleave { get; private set; }

        public GrainTypeData(Type type)
        {
            Type = type;
            this.IsReentrant = type.IsDefined(typeof(ReentrantAttribute), true);
            // TODO: shouldn't this use GrainInterfaceUtils.IsStatelessWorker?
            this.IsStatelessWorker = type.IsDefined(typeof(StatelessWorkerAttribute), true);
            this.GrainClass = TypeUtils.GetFullName(type);
            RemoteInterfaceTypes = GetRemoteInterfaces(type);
            this.MayInterleave = GetMayInterleavePredicate(type) ?? (_ => false);
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
                where T : Attribute
        {
            var attribs = grainInterface.GetCustomAttributes<T>(inherit: true).ToArray();
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

        internal static PlacementStrategy GetPlacementStrategy(Type grainClass, PlacementStrategy defaultPlacement)
        {
            PlacementStrategy placement;

            if (GetPlacementStrategy<PlacementAttribute>(
                grainClass,
                a => a.PlacementStrategy,
                out placement))
            {
                return placement;
            }

            return defaultPlacement;
        }

        internal static string GetGrainDirectory(Type grainClass)
        {
            var attr = grainClass.GetCustomAttribute<GrainDirectoryAttribute>();
            return attr != default ? attr.GrainDirectoryName : GrainDirectoryAttribute.DEFAULT_GRAIN_DIRECTORY;
        }

        public GrainTypeData MakeGenericType(Type[] typeArgs)
        {
            // Need to make a non-generic instance of the class to access the static data field. The field itself is independent of the instantiated type.
            var concreteActivationType = this.Type.MakeGenericType(typeArgs);
            return new GrainTypeData(concreteActivationType);
        }

        /// <summary>
        /// Returns interleave predicate depending on whether class is marked with <see cref="MayInterleaveAttribute"/> or not.
        /// </summary>
        /// <param name="grainType">Grain class.</param>
        /// <returns></returns>
        private static Func<InvokeMethodRequest, bool> GetMayInterleavePredicate(Type grainType)
        {
            var attribute = grainType.GetCustomAttribute<MayInterleaveAttribute>();
            if (attribute is null)
                return null;

            if (grainType.IsDefined(typeof(ReentrantAttribute), true))
                throw new InvalidOperationException(
                    $"Class {grainType.FullName} is already marked with Reentrant attribute");

            var callbackMethodName = attribute.CallbackMethodName;
            var method = grainType.GetMethod(callbackMethodName, BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
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
