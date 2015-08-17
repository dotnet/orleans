/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using System;
using System.Linq;
using System.Threading.Tasks;

using Orleans.CodeGeneration;
using Orleans.Concurrency;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.Scheduler;
using Orleans.Runtime.ConsistentRing;
using Orleans.Streams;

namespace Orleans.Runtime.Providers
{
    internal class SiloProviderRuntime : ISiloSideStreamProviderRuntime
    { 
        private static volatile SiloProviderRuntime instance;
        private static readonly object syncRoot = new Object();

        private IStreamPubSub pubSub;
        private ImplicitStreamSubscriberTable implicitStreamSubscriberTable;
        private IPersistentStreamPullingManager pullingAgentManager;

        public IGrainFactory GrainFactory { get; private set; }
        public Guid ServiceId { get; private set; }
        public string SiloIdentity { get; private set; }

        private SiloProviderRuntime()
        {
        }

        internal static void Initialize(GlobalConfiguration config, string siloIdentity, IGrainFactory grainFactory)
        {
            Instance.ServiceId = config.ServiceId;
            Instance.SiloIdentity = siloIdentity;
            Instance.GrainFactory = grainFactory;
        }

        public static SiloProviderRuntime Instance
        {
            get
            {
                if (instance == null)
                {
                    lock (syncRoot)
                    {
                        if (instance == null)
                            instance = new SiloProviderRuntime();
                    }
                }
                return instance;
            }
        }

        public ImplicitStreamSubscriberTable ImplicitStreamSubscriberTable { get { return implicitStreamSubscriberTable; } }

        public static void StreamingInitialize(IGrainFactory grainFactory, ImplicitStreamSubscriberTable implicitStreamSubscriberTable) 
        {
            Instance.implicitStreamSubscriberTable = implicitStreamSubscriberTable;
            Instance.pubSub = new StreamPubSubImpl(new GrainBasedPubSubRuntime(grainFactory), implicitStreamSubscriberTable);
        }

        public StreamDirectory GetStreamDirectory()
        {
            var currentActivation = GetCurrentActivationData();
            return currentActivation.GetStreamDirectory();
        }

        public Logger GetLogger(string loggerName)
        {
            return TraceLogger.GetLogger(loggerName, TraceLogger.LoggerType.Provider);
        }

        public string ExecutingEntityIdentity()
        {
            var currentActivation = GetCurrentActivationData();
            return currentActivation.Address.ToString();
        }

        public SiloAddress ExecutingSiloAddress { get { return Silo.CurrentSilo.SiloAddress; } }

        public void RegisterSystemTarget(ISystemTarget target)
        {
            Silo.CurrentSilo.RegisterSystemTarget((SystemTarget)target);
        }

        public void UnRegisterSystemTarget(ISystemTarget target)
        {
            Silo.CurrentSilo.UnregisterSystemTarget((SystemTarget)target);
        }

        public IDisposable RegisterTimer(Func<object, Task> asyncCallback, object state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = GrainTimer.FromTaskCallback(asyncCallback, state, dueTime, period);
            timer.Start();
            return timer;
        }

        public IStreamPubSub PubSub(StreamPubSubType pubSubType)
        {
            switch (pubSubType)
            {
                case StreamPubSubType.GrainBased:
                    return pubSub;
                default:
                    return null;
            }
        }

        public IConsistentRingProviderForGrains GetConsistentRingProvider(int mySubRangeIndex, int numSubRanges)
        {
            return new EquallyDevidedRangeRingProvider(InsideRuntimeClient.Current.ConsistentRingProvider, mySubRangeIndex, numSubRanges);
        }

        public bool InSilo { get { return true; } }

        public Task<Tuple<TExtension, TExtensionInterface>> BindExtension<TExtension, TExtensionInterface>(Func<TExtension> newExtensionFunc)
            where TExtension : IGrainExtension
            where TExtensionInterface : IGrainExtension
        {
            // Hookup Extension.
            TExtension extension;
            if (!TryGetExtensionHandler(out extension))
            {
                extension = newExtensionFunc();
                if (!TryAddExtension(extension))
                    throw new OrleansException("Failed to register " + typeof(TExtension).Name);
            }

            IAddressable currentGrain = RuntimeClient.Current.CurrentActivationData.GrainInstance;
            var currentTypedGrain = currentGrain.AsReference<TExtensionInterface>();

            return Task.FromResult(Tuple.Create(extension, currentTypedGrain));
        }

        /// <summary>
        /// Adds the specified extension handler to the currently running activation.
        /// This method must be called during an activation turn.
        /// </summary>
        /// <param name="handler"></param>
        /// <returns></returns>
        internal bool TryAddExtension(IGrainExtension handler)
        {
            var currentActivation = GetCurrentActivationData();
            var invoker = TryGetExtensionInvoker(handler.GetType());
            if (invoker == null)
                throw new SystemException("Extension method invoker was not generated for an extension interface");
            
            return currentActivation.TryAddExtension(invoker, handler);
        }

        private static ActivationData GetCurrentActivationData()
        {
            var context = RuntimeContext.Current.ActivationContext as SchedulingContext;
            if (context == null || context.Activation == null)
                throw new InvalidOperationException("Attempting to GetCurrentActivationData when not in an activation scope");
            
            var currentActivation = context.Activation;
            return currentActivation;
        }

        /// <summary>
        /// Removes the specified extension handler (and any other extension that implements the same interface ID)
        /// from the currently running activation.
        /// This method must be called during an activation turn.
        /// </summary>
        /// <param name="handler"></param>
        internal void RemoveExtension(IGrainExtension handler)
        {
            var currentActivation = GetCurrentActivationData();
            currentActivation.RemoveExtension(handler);
        }

        internal bool TryGetExtensionHandler<TExtension>(out TExtension result)
        {
            var currentActivation = GetCurrentActivationData();
            IGrainExtension untypedResult;
            if (currentActivation.TryGetExtensionHandler(typeof(TExtension), out untypedResult))
            {
                result = (TExtension)untypedResult;
                return true;
            }
            
            result = default(TExtension);
            return false;
        }

        private static IGrainExtensionMethodInvoker TryGetExtensionInvoker(Type handlerType)
        {
            var interfaces = CodeGeneration.GrainInterfaceData.GetRemoteInterfaces(handlerType).Values;
            if(interfaces.Count != 1)
                throw new InvalidOperationException(String.Format("Extension type {0} implements more than one grain interface.", handlerType.FullName));

            var interfaceId = CodeGeneration.GrainInterfaceData.ComputeInterfaceId(interfaces.First());
            var invoker = GrainTypeManager.Instance.GetInvoker(interfaceId);
            if (invoker != null)
                return (IGrainExtensionMethodInvoker) invoker;
            
            throw new ArgumentException("Provider extension handler type " + handlerType + " was not found in the type manager", "handler");
        }
        
        public Task InvokeWithinSchedulingContextAsync(Func<Task> asyncFunc, object context)
        {
            if (null == asyncFunc)
                throw new ArgumentNullException("asyncFunc");
            if (null == context)
                throw new ArgumentNullException("context");
            if (!(context is ISchedulingContext))
                throw new ArgumentException("context object is not of a ISchedulingContext type.", "context");

            // copied from InsideRuntimeClient.ExecAsync().
            return OrleansTaskScheduler.Instance.RunOrQueueTask(asyncFunc, (ISchedulingContext) context);
        }

        public object GetCurrentSchedulingContext()
        {
            return RuntimeContext.CurrentActivationContext;
        }

        public Task InitializePullingAgents(
            string streamProviderName,
            StreamQueueBalancerType balancerType,
            StreamPubSubType pubSubType,
            IQueueAdapterFactory adapterFactory,
            IQueueAdapter queueAdapter,
            TimeSpan getQueueMsgsTimerPeriod,
            TimeSpan initQueueTimeout,
            TimeSpan maxEventDeliveryTime)
        {
            IStreamQueueBalancer queueBalancer = StreamQueueBalancerFactory.Create(
                balancerType, streamProviderName, Silo.CurrentSilo.LocalSiloStatusOracle, Silo.CurrentSilo.OrleansConfig, this, adapterFactory.GetStreamQueueMapper());
            var managerId = GrainId.NewSystemTargetGrainIdByTypeCode(Constants.PULLING_AGENTS_MANAGER_SYSTEM_TARGET_TYPE_CODE);
            var manager = new PersistentStreamPullingManager(managerId, streamProviderName, this, this.PubSub(pubSubType), adapterFactory, queueBalancer, getQueueMsgsTimerPeriod, initQueueTimeout, maxEventDeliveryTime);
            this.RegisterSystemTarget(manager);
            // Init the manager only after it was registered locally.
            pullingAgentManager = manager.AsReference<IPersistentStreamPullingManager>();
            // Need to call it as a grain reference though.
            return pullingAgentManager.Initialize(queueAdapter.AsImmutable());
        }

        public Task StartPullingAgents()
        {
            return pullingAgentManager.StartAgents();
        }

        public Task StopPullingAgents()
        {
            return pullingAgentManager.StopAgents();
        }
    }
}
