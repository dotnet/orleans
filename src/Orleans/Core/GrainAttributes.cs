using System;
using Orleans.GrainDirectory;
using Orleans.Runtime;

namespace Orleans
{
    namespace Concurrency
    {
        /// <summary>
        /// The ReadOnly attribute is used to mark methods that do not modify the state of a grain.
        /// <para>
        /// Marking methods as ReadOnly allows the run-time system to perform a number of optimizations
        /// that may significantly improve the performance of your application.
        /// </para>
        /// </summary>
        [AttributeUsage(AttributeTargets.Method)]
        internal sealed class ReadOnlyAttribute : Attribute
        {
        }

        /// <summary>
        /// The Reentrant attribute is used to mark grain implementation classes that allow request interleaving within a task.
        /// <para>
        /// This is an advanced feature and should not be used unless the implications are fully understood.
        /// That said, allowing request interleaving allows the run-time system to perform a number of optimizations
        /// that may significantly improve the performance of your application. 
        /// </para>
        /// </summary>
        [AttributeUsage(AttributeTargets.Class)]
        public sealed class ReentrantAttribute : Attribute
        {
        }

        /// <summary>
        /// The Unordered attribute is used to mark grain interface in which the delivery order of
        /// messages is not significant.
        /// </summary>
        [AttributeUsage(AttributeTargets.Interface)]
        public sealed class UnorderedAttribute : Attribute
        {
        }

        /// <summary>
        /// The StatelessWorker attribute is used to mark grain class in which there is no expectation
        /// of preservation of grain state between requests and where multiple activations of the same grain are allowed to be created by the runtime. 
        /// </summary>
        [AttributeUsage(AttributeTargets.Class)]
        public sealed class StatelessWorkerAttribute : Attribute
        {
            /// <summary>
            /// Maximal number of local StatelessWorkers in a single silo.
            /// </summary>
            public int MaxLocalWorkers { get; private set; }

            public StatelessWorkerAttribute(int maxLocalWorkers)
            {
                MaxLocalWorkers = maxLocalWorkers;
            }

            public StatelessWorkerAttribute()
            {
                MaxLocalWorkers = -1;
            }
        }

        /// <summary>
        /// The AlwaysInterleaveAttribute attribute is used to mark methods that can interleave with any other method type, including write (non ReadOnly) requests.
        /// </summary>
        /// <remarks>
        /// Note that this attribute is applied to method declaration in the grain interface, 
        /// and not to the method in the implementation class itself.
        /// </remarks>
        [AttributeUsage(AttributeTargets.Method)]
        public sealed class AlwaysInterleaveAttribute : Attribute
        {
        }

        /// <summary>
        /// The MayInterleaveAttribute attribute is used to mark classes 
        /// that want to control request interleaving via supplied method callback.
        /// </summary>
        /// <remarks>
        /// The callback method name should point to a public static function declared on the same class 
        /// and having the following signature: <c>public static bool MayInterleave(InvokeMethodRequest req)</c>
        /// </remarks>
        [AttributeUsage(AttributeTargets.Class)]
        public sealed class MayInterleaveAttribute : Attribute
        {
            /// <summary>
            /// The name of the callback method
            /// </summary>
            internal string CallbackMethodName { get; private set; }

            public MayInterleaveAttribute(string callbackMethodName)
            {
                CallbackMethodName = callbackMethodName;
            }
        }

        /// <summary>
        /// The Immutable attribute indicates that instances of the marked class or struct are never modified
        /// after they are created.
        /// </summary>
        /// <remarks>
        /// Note that this implies that sub-objects are also not modified after the instance is created.
        /// </remarks>
        [AttributeUsage(AttributeTargets.Struct | AttributeTargets.Class)]
        public sealed class ImmutableAttribute : Attribute
        {
        }
    }

    namespace MultiCluster
    {
        /// <summary>
        /// base class for multi cluster registration strategies.
        /// </summary>
        [AttributeUsage(AttributeTargets.Class)]
        public abstract class RegistrationAttribute : Attribute
        {
            internal MultiClusterRegistrationStrategy RegistrationStrategy { get; private set; }

            internal RegistrationAttribute(MultiClusterRegistrationStrategy strategy)
            {
                RegistrationStrategy = strategy ?? MultiClusterRegistrationStrategy.GetDefault();
            }
        }

        /// <summary>
        /// This attribute indicates that instances of the marked grain class will have a single instance across all available clusters. Any requests in any clusters will be forwarded to the single activation instance.
        /// </summary>
        public class GlobalSingleInstanceAttribute : RegistrationAttribute
        {
            public GlobalSingleInstanceAttribute()
                : base(GlobalSingleInstanceRegistration.Singleton)
            {
            }
        }

        /// <summary>
        /// This attribute indicates that instances of the marked grain class
        /// will have an independent instance for each cluster with 
        /// no coordination. 
        /// </summary>
        public class OneInstancePerClusterAttribute : RegistrationAttribute
        {
            public OneInstancePerClusterAttribute()
                : base(ClusterLocalRegistration.Singleton)
            {
            }
        }
    }

    namespace Placement
    {
        using Orleans.Runtime;

        /// <summary>
        /// Base for all placement policy marker attributes.
        /// </summary>
        [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
        public abstract class PlacementAttribute : Attribute
        {
            internal PlacementStrategy PlacementStrategy { get; private set; }

            internal PlacementAttribute(PlacementStrategy placement)
            {
                if (placement == null) throw new ArgumentNullException(nameof(placement));

                PlacementStrategy = placement;
            }
        }

        /// <summary>
        /// Marks a grain class as using the <c>RandomPlacement</c> policy.
        /// </summary>
        /// <remarks>
        /// This is the default placement policy, so this attribute does not need to be used for normal grains.
        /// </remarks>
        [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
        public sealed class RandomPlacementAttribute : PlacementAttribute
        {
            public RandomPlacementAttribute() :
                base(RandomPlacement.Singleton)
            { }
        }

        /// <summary>
        /// Marks a grain class as using the <c>PreferLocalPlacement</c> policy.
        /// </summary>
        [AttributeUsage(AttributeTargets.Class, AllowMultiple = false) ]
        public sealed class PreferLocalPlacementAttribute : PlacementAttribute
        {
            public PreferLocalPlacementAttribute() :
                base(PreferLocalPlacement.Singleton)
            { }
        }

        /// <summary>
        /// Marks a grain class as using the <c>ActivationCountBasedPlacement</c> policy.
        /// </summary>
        [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
        public sealed class ActivationCountBasedPlacementAttribute : PlacementAttribute
        {
            public ActivationCountBasedPlacementAttribute() :
                base(ActivationCountBasedPlacement.Singleton)
            { }
        }
    }

    namespace CodeGeneration
    {
        /// <summary>
        /// The TypeCodeOverrideAttribute attribute allows to specify the grain interface ID or the grain class type code
        /// to override the default ones to avoid hash collisions
        /// </summary>
        [AttributeUsage(AttributeTargets.Interface | AttributeTargets.Class)]
        public sealed class TypeCodeOverrideAttribute : Attribute
        {
            /// <summary>
            /// Use a specific grain interface ID or grain class type code (e.g. to avoid hash collisions)
            /// </summary>
            public int TypeCode { get; private set; }

            public TypeCodeOverrideAttribute(int typeCode)
            {
                TypeCode = typeCode;
            }
        }

        /// <summary>
        /// Specifies the method id for the interface method which this attribute is declared on.
        /// </summary>
        /// <remarks>
        /// Method ids must be unique for all methods in a given interface.
        /// This attribute is only applicable for interface method declarations, not for method definitions on classes.
        /// </remarks>
        [AttributeUsage(AttributeTargets.Method)]
        public sealed class MethodIdAttribute : Attribute
        {
            /// <summary>
            /// Gets the method id for the interface method this attribute is declared on.
            /// </summary>
            public int MethodId { get; }

            /// <summary>
            /// Specifies the method id for the interface method which this attribute is declared on.
            /// </summary>
            /// <remarks>
            /// Method ids must be unique for all methods in a given interface.
            /// This attribute is only valid only on interface method declarations, not on method definitions.
            /// </remarks>
            /// <param name="methodId">The method id.</param>
            public MethodIdAttribute(int methodId)
            {
                this.MethodId = methodId;
            }
        }

    /// <summary>
    /// Used to mark a method as providing a copier function for that type.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class CopierMethodAttribute : Attribute
    {
    }

    /// <summary>
    /// Used to mark a method as providing a serializer function for that type.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class SerializerMethodAttribute : Attribute
    {
    }

    /// <summary>
    /// Used to mark a method as providing a deserializer function for that type.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class DeserializerMethodAttribute : Attribute
    {
    }

    /// <summary>
    /// Used to make a class for auto-registration as a serialization helper.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class)]
    [Obsolete("[RegisterSerializer] is obsolete, please use [Serializer(typeof(TargetType))] instead.")]
    public sealed class RegisterSerializerAttribute : Attribute
    {
    }
    }

    namespace Providers
    {
        /// <summary>
        /// The [Orleans.Providers.StorageProvider] attribute is used to define which storage provider to use for persistence of grain state.
        /// <para>
        /// Specifying [Orleans.Providers.StorageProvider] property is recommended for all grains which extend Grain&lt;T&gt;.
        /// If no [Orleans.Providers.StorageProvider] attribute is  specified, then a "Default" strorage provider will be used.
        /// If a suitable storage provider cannot be located for this grain, then the grain will fail to load into the Silo.
        /// </para>
        /// </summary>
        [AttributeUsage(AttributeTargets.Class)]
        public sealed class StorageProviderAttribute : Attribute
        {
            /// <summary>
            /// The name of the provider to be used for persisting of grain state
            /// </summary>
            public string ProviderName { get; set; }

            public StorageProviderAttribute()
            {
                ProviderName = Runtime.Constants.DEFAULT_STORAGE_PROVIDER_NAME;
            }
        }

        /// <summary>
        /// The [Orleans.Providers.LogConsistencyProvider] attribute is used to define which consistency provider to use for grains using the log-view state abstraction.
        /// <para>
        /// Specifying [Orleans.Providers.LogConsistencyProvider] property is recommended for all grains that derive
        /// from ILogConsistentGrain, such as JournaledGrain.
        /// If no [Orleans.Providers.LogConsistencyProvider] attribute is  specified, then the runtime tries to locate
        /// one as follows. First, it looks for a 
        /// "Default" provider in the configuration file, then it checks if the grain type defines a default.
        /// If a consistency provider cannot be located for this grain, then the grain will fail to load into the Silo.
        /// </para>
        /// </summary>
        [AttributeUsage(AttributeTargets.Class)]
        public sealed class LogConsistencyProviderAttribute : Attribute
        {
            /// <summary>
            /// The name of the provider to be used for consistency
            /// </summary>
            public string ProviderName { get; set; }

            public LogConsistencyProviderAttribute()
            {
                ProviderName = Runtime.Constants.DEFAULT_LOG_CONSISTENCY_PROVIDER_NAME;
            }
        }

    }

    [AttributeUsage(AttributeTargets.Class, AllowMultiple=true)]
    public sealed class ImplicitStreamSubscriptionAttribute : Attribute
    {
        public string Namespace { get; private set; }
        
        // We have not yet come to an agreement whether the provider should be specified as well.
        public ImplicitStreamSubscriptionAttribute(string streamNamespace)
        {
            Namespace = streamNamespace;
        }
    }
}
