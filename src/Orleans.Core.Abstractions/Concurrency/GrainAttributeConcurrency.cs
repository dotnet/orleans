using Orleans.CodeGeneration;
using Orleans.Metadata;
using Orleans.Placement;
using Orleans.Runtime;
using System;
using System.Collections.Generic;

namespace Orleans.Concurrency
{
    /// <summary>
    /// The ReadOnly attribute is used to mark methods that do not modify the state of a grain.
    /// <para>
    /// Marking methods as ReadOnly allows the run-time system to perform a number of optimizations
    /// that may significantly improve the performance of your application.
    /// </para>
    /// </summary>
    [InvokableCustomInitializer(nameof(IRequest.AddInvokeMethodOptions), InvokeMethodOptions.ReadOnly)]
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class ReadOnlyAttribute : Attribute
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
    public sealed class ReentrantAttribute : Attribute, IGrainPropertiesProviderAttribute
    {
        /// <inheritdoc/>
        public void Populate(IServiceProvider services, Type grainClass, GrainType grainType, Dictionary<string, string> properties)
        {
            properties[WellKnownGrainTypeProperties.Reentrant] = "true";
        }
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
    public sealed class StatelessWorkerAttribute : PlacementAttribute, IGrainPropertiesProviderAttribute
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="StatelessWorkerAttribute"/> class.
        /// </summary>
        /// <param name="maxLocalWorkers">
        /// The maximum local workers.
        /// </param>
        public StatelessWorkerAttribute(int maxLocalWorkers)
            : base(new StatelessWorkerPlacement(maxLocalWorkers))
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="StatelessWorkerAttribute"/> class.
        /// </summary>
        public StatelessWorkerAttribute()
            : base(new StatelessWorkerPlacement())
        {
        }

        /// <inheritdoc/>
        public override void Populate(IServiceProvider services, Type grainClass, GrainType grainType, Dictionary<string, string> properties)
        {
            base.Populate(services, grainClass, grainType, properties);
            properties[WellKnownGrainTypeProperties.Unordered] = "true";
        }
    }

    /// <summary>
    /// The AlwaysInterleaveAttribute attribute is used to mark methods that can interleave with any other method type, including write (non ReadOnly) requests.
    /// </summary>
    /// <remarks>
    /// Note that this attribute is applied to method declaration in the grain interface,
    /// and not to the method in the implementation class itself.
    /// </remarks>
    [InvokableCustomInitializer(nameof(IRequest.AddInvokeMethodOptions), InvokeMethodOptions.AlwaysInterleave)]
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
    /// and having the following signature: <c>public static bool MayInterleave(IInvokable req)</c>
    /// </remarks>
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class MayInterleaveAttribute : Attribute, IGrainPropertiesProviderAttribute
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="MayInterleaveAttribute"/> class.
        /// </summary>
        /// <param name="callbackMethodName">
        /// The callback method name. This should resolve to a method with the 
        /// following signature: <c>public static bool NameOfMethod(IInvokable req)</c>
        /// </param>
        public MayInterleaveAttribute(string callbackMethodName)
        {
            this.CallbackMethodName = callbackMethodName;
        }

        /// <summary>
        /// Gets the name of the callback method
        /// </summary>
        internal string CallbackMethodName { get; private set; }


        /// <inheritdoc/>
        public void Populate(IServiceProvider services, Type grainClass, GrainType grainType, Dictionary<string, string> properties)
        {
            properties[WellKnownGrainTypeProperties.MayInterleavePredicate] = this.CallbackMethodName;
        }
    }

    /// <summary>
    /// Indicates that a method on a grain interface is one-way and that no response message will be sent to the caller.
    /// </summary>
    [InvokableCustomInitializer(nameof(IRequest.AddInvokeMethodOptions), InvokeMethodOptions.OneWay)]
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class OneWayAttribute : Attribute
    {
    }
}
