using System;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.Messaging;
using Orleans.Providers;
using Orleans.CodeGeneration;
using Orleans.Serialization;
using Orleans.Storage;
using Orleans.Runtime.Configuration;
using System.Collections.Concurrent;
using Orleans.Streams;

namespace Orleans
{
    internal class OutsideRuntimeClient : IRuntimeClient, IDisposable
    {

        internal static bool TestOnlyThrowExceptionDuringInit { get; set; }

        private readonly TraceLogger logger;
        private readonly TraceLogger appLogger;

        private readonly ClientConfiguration config;

        private readonly ConcurrentDictionary<CorrelationId, CallbackData> callbacks;
        private readonly ConcurrentDictionary<GuidId, LocalObjectData> localObjects;

        private readonly ProxiedMessageCenter transport;
        private bool listenForMessages;
        private CancellationTokenSource listeningCts;

        private readonly ClientProviderRuntime clientProviderRuntime;
        private readonly StatisticsProviderManager statisticsProviderManager;

        internal ClientStatisticsManager ClientStatistics;
        private readonly GrainId clientId;
        private GrainInterfaceMap grainInterfaceMap;
        private readonly ThreadTrackingStatistic incomingMessagesThreadTimeTracking;

        // initTimeout used to be AzureTableDefaultPolicies.TableCreationTimeout, which was 3 min
        private static readonly TimeSpan initTimeout = TimeSpan.FromMinutes(1);
        private static readonly TimeSpan resetTimeout = TimeSpan.FromMinutes(1);

        private const string BARS = "----------";

        private readonly GrainFactory grainFactory;

        public GrainFactory InternalGrainFactory
        {
            get { return grainFactory; }
        }

        /// <summary>
        /// Response timeout.
        /// </summary>
        private TimeSpan responseTimeout;

        private static readonly Object staticLock = new Object();

        Logger IRuntimeClient.AppLogger
        {
            get { return appLogger; }
        }

        public ActivationAddress CurrentActivationAddress
        {
            get;
            private set;
        }

        public SiloAddress CurrentSilo
        {
            get { return CurrentActivationAddress.Silo; }
        }

        public string Identity
        {
            get { return CurrentActivationAddress.ToString(); }
        }

        public IActivationData CurrentActivationData
        {
            get { return null; }
        }

        public IAddressable CurrentGrain
        {
            get { return null; }
        }

        public IStorageProvider CurrentStorageProvider
        {
            get { throw new InvalidOperationException("Storage provider only available from inside grain"); }
        }

        internal IList<Uri> Gateways
        {
            get
            {
                return transport.GatewayManager.ListProvider.GetGateways().GetResult();
            }
        }

        public IStreamProviderManager CurrentStreamProviderManager { get; private set; }

        public IStreamProviderRuntime CurrentStreamProviderRuntime
        {
            get { return clientProviderRuntime; }
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Reliability", "CA2000:Dispose objects before losing scope",
            Justification = "MessageCenter is IDisposable but cannot call Dispose yet as it lives past the end of this method call.")]
        public OutsideRuntimeClient(ClientConfiguration cfg, GrainFactory grainFactory, bool secondary = false)
        {
            this.grainFactory = grainFactory;
            this.clientId = GrainId.NewClientId();

            if (cfg == null)
            {
                Console.WriteLine("An attempt to create an OutsideRuntimeClient with null ClientConfiguration object.");
                throw new ArgumentException("OutsideRuntimeClient was attempted to be created with null ClientConfiguration object.", "cfg");
            }

            this.config = cfg;

            if (!TraceLogger.IsInitialized) TraceLogger.Initialize(config);
            StatisticsCollector.Initialize(config);
            SerializationManager.Initialize(config.UseStandardSerializer, cfg.SerializationProviders, config.UseJsonFallbackSerializer);
            logger = TraceLogger.GetLogger("OutsideRuntimeClient", TraceLogger.LoggerType.Runtime);
            appLogger = TraceLogger.GetLogger("Application", TraceLogger.LoggerType.Application);

            try
            {
                LoadAdditionalAssemblies();
                
                PlacementStrategy.Initialize();

                callbacks = new ConcurrentDictionary<CorrelationId, CallbackData>();
                localObjects = new ConcurrentDictionary<GuidId, LocalObjectData>();

                if (!secondary)
                {
                    UnobservedExceptionsHandlerClass.SetUnobservedExceptionHandler(UnhandledException);
                }
                AppDomain.CurrentDomain.DomainUnload += CurrentDomain_DomainUnload;

                // Ensure SerializationManager static constructor is called before AssemblyLoad event is invoked
                SerializationManager.GetDeserializer(typeof(String));

                clientProviderRuntime = new ClientProviderRuntime(grainFactory, new DefaultServiceProvider());
                statisticsProviderManager = new StatisticsProviderManager("Statistics", clientProviderRuntime);
                var statsProviderName = statisticsProviderManager.LoadProvider(config.ProviderConfigurations)
                    .WaitForResultWithThrow(initTimeout);
                if (statsProviderName != null)
                {
                    config.StatisticsProviderName = statsProviderName;
                }

                responseTimeout = Debugger.IsAttached ? Constants.DEFAULT_RESPONSE_TIMEOUT : config.ResponseTimeout;
                BufferPool.InitGlobalBufferPool(config);
                var localAddress = ClusterConfiguration.GetLocalIPAddress(config.PreferredFamily, config.NetInterface);

                // Client init / sign-on message
                logger.Info(ErrorCode.ClientInitializing, string.Format(
                    "{0} Initializing OutsideRuntimeClient on {1} at {2} Client Id = {3} {0}",
                    BARS, config.DNSHostName, localAddress, clientId));
                string startMsg = string.Format("{0} Starting OutsideRuntimeClient with runtime Version='{1}' in AppDomain={2}", 
                    BARS, RuntimeVersion.Current, PrintAppDomainDetails());
                startMsg = string.Format("{0} Config= "  + Environment.NewLine + " {1}", startMsg, config);
                logger.Info(ErrorCode.ClientStarting, startMsg);

                if (TestOnlyThrowExceptionDuringInit)
                {
                    throw new Exception("TestOnlyThrowExceptionDuringInit");
                }

                config.CheckGatewayProviderSettings();

                var generation = -SiloAddress.AllocateNewGeneration(); // Client generations are negative
                var gatewayListProvider = GatewayProviderFactory.CreateGatewayListProvider(config)
                    .WithTimeout(initTimeout).Result;
                transport = new ProxiedMessageCenter(config, localAddress, generation, clientId, gatewayListProvider);
                
                if (StatisticsCollector.CollectThreadTimeTrackingStats)
                {
                    incomingMessagesThreadTimeTracking = new ThreadTrackingStatistic("ClientReceiver");
                }
            }
            catch (Exception exc)
            {
                if (logger != null) logger.Error(ErrorCode.Runtime_Error_100319, "OutsideRuntimeClient constructor failed.", exc);
                ConstructorReset();
                throw;
            }
        }

        private void StreamingInitialize()
        {
            var implicitSubscriberTable = transport.GetImplicitStreamSubscriberTable(grainFactory).Result;
            clientProviderRuntime.StreamingInitialize(implicitSubscriberTable);
            var streamProviderManager = new Streams.StreamProviderManager();
            streamProviderManager
                .LoadStreamProviders(
                    this.config.ProviderConfigurations,
                    clientProviderRuntime)
                .Wait();
            CurrentStreamProviderManager = streamProviderManager;
        }

        private static void LoadAdditionalAssemblies()
        {
            var logger = TraceLogger.GetLogger("AssemblyLoader.Client", TraceLogger.LoggerType.Runtime);

            var directories =
                new Dictionary<string, SearchOption>
                    {
                        {
                            Path.GetDirectoryName(typeof(OutsideRuntimeClient).GetTypeInfo().Assembly.Location), 
                            SearchOption.AllDirectories
                        }
                    };
            var excludeCriteria =
                new AssemblyLoaderPathNameCriterion[]
                    {
                        AssemblyLoaderCriteria.ExcludeResourceAssemblies,
                        AssemblyLoaderCriteria.ExcludeSystemBinaries()
                    };
            var loadProvidersCriteria =
                new AssemblyLoaderReflectionCriterion[]
                    {
                        AssemblyLoaderCriteria.LoadTypesAssignableFrom(typeof(IProvider))
                    };

            AssemblyLoader.LoadAssemblies(directories, excludeCriteria, loadProvidersCriteria, logger);
        }
        
        private void UnhandledException(ISchedulingContext context, Exception exception)
        {
            logger.Error(ErrorCode.Runtime_Error_100007, String.Format("OutsideRuntimeClient caught an UnobservedException."), exception);
            logger.Assert(ErrorCode.Runtime_Error_100008, context == null, "context should be not null only inside OrleansRuntime and not on the client.");
        }

        public void Start()
        {
            lock (staticLock)
            {
                if (RuntimeClient.Current != null)
                    throw new InvalidOperationException("Can only have one RuntimeClient per AppDomain");
                RuntimeClient.Current = this;
            }
            StartInternal();

            logger.Info(ErrorCode.ProxyClient_StartDone, "{0} Started OutsideRuntimeClient with Global Client ID: {1}", BARS, CurrentActivationAddress.ToString() + ", client GUID ID: " + clientId);
              
        }

        // used for testing to (carefully!) allow two clients in the same process
        internal void StartInternal()
        {
            transport.Start();
            TraceLogger.MyIPEndPoint = transport.MyAddress.Endpoint; // transport.MyAddress is only set after transport is Started.
            CurrentActivationAddress = ActivationAddress.NewActivationAddress(transport.MyAddress, clientId);

            ClientStatistics = new ClientStatisticsManager(config);
            ClientStatistics.Start(config, statisticsProviderManager, transport, clientId)
                .WaitWithThrow(initTimeout);

            listeningCts = new CancellationTokenSource();
            var ct = listeningCts.Token;
            listenForMessages = true;

            // Keeping this thread handling it very simple for now. Just queue task on thread pool.
            Task.Factory.StartNew(() => 
                {
                    try
                    {
                        RunClientMessagePump(ct);
                    }
                    catch(Exception exc)
                    {
                        logger.Error(ErrorCode.Runtime_Error_100326, "RunClientMessagePump has thrown exception", exc);
                    }
                }
            );
            grainInterfaceMap = transport.GetTypeCodeMap(grainFactory).Result;
            StreamingInitialize();
        }

        private void RunClientMessagePump(CancellationToken ct)
        {
            if (StatisticsCollector.CollectThreadTimeTrackingStats)
            {
                incomingMessagesThreadTimeTracking.OnStartExecution();
            }
            while (listenForMessages)
            {
                var message = transport.WaitMessage(Message.Categories.Application, ct);

                if (message == null) // if wait was cancelled
                    break;
#if TRACK_DETAILED_STATS
                        if (StatisticsCollector.CollectThreadTimeTrackingStats)
                        {
                            incomingMessagesThreadTimeTracking.OnStartProcessing();
                        }
#endif
                switch (message.Direction)
                {
                    case Message.Directions.Response:
                        {
                            ReceiveResponse(message);
                            break;
                        }
                    case Message.Directions.OneWay:
                    case Message.Directions.Request:
                        {
                            this.DispatchToLocalObject(message);
                            break;
                        }
                    default:
                        logger.Error(ErrorCode.Runtime_Error_100327, String.Format("Message not supported: {0}.", message));
                        break;
                }
#if TRACK_DETAILED_STATS
                        if (StatisticsCollector.CollectThreadTimeTrackingStats)
                        {
                            incomingMessagesThreadTimeTracking.OnStopProcessing();
                            incomingMessagesThreadTimeTracking.IncrementNumberOfProcessed();
                        }
#endif
            }
            if (StatisticsCollector.CollectThreadTimeTrackingStats)
            {
                incomingMessagesThreadTimeTracking.OnStopExecution();
            }
        }

        private void DispatchToLocalObject(Message message)
        {
            LocalObjectData objectData;
            GuidId observerId = message.TargetObserverId;
            if (observerId == null)
            {                
                logger.Error(
                    ErrorCode.ProxyClient_OGC_TargetNotFound_2,
                    String.Format("Did not find TargetObserverId header in the message = {0}. A request message to a client is expected to have an observerId.", message));
                return;
            }

            if (localObjects.TryGetValue(observerId, out objectData))
                this.InvokeLocalObjectAsync(objectData, message);
            else
            {
                logger.Error(
                    ErrorCode.ProxyClient_OGC_TargetNotFound,
                    String.Format(
                        "Unexpected target grain in request: {0}. Message={1}",
                        message.TargetGrain, 
                        message));
            }
        }

        private void InvokeLocalObjectAsync(LocalObjectData objectData, Message message)
        {
            var obj = (IAddressable)objectData.LocalObject.Target;
            if (obj == null)
            {
                //// Remove from the dictionary record for the garbage collected object? But now we won't be able to detect invalid dispatch IDs anymore.
                logger.Warn(ErrorCode.Runtime_Error_100162,
                    String.Format("Object associated with Observer ID {0} has been garbage collected. Deleting object reference and unregistering it. Message = {1}", objectData.ObserverId, message));

                LocalObjectData ignore;
                // Try to remove. If it's not there, we don't care.
                localObjects.TryRemove(objectData.ObserverId, out ignore);
                return;
            }

            bool start;
            lock (objectData.Messages)
            {
                objectData.Messages.Enqueue(message);
                start = !objectData.Running;
                objectData.Running = true;
            }
            if (logger.IsVerbose) logger.Verbose("InvokeLocalObjectAsync {0} start {1}", message, start);
            if (start)
            {
                // we use Task.Run() to ensure that the message pump operates asynchronously
                // with respect to the current thread. see 
                // http://channel9.msdn.com/Events/TechEd/Europe/2013/DEV-B317#fbid=aIWUq0ssW74
                // at position 54:45. 
                //
                // according to the information posted at:
                // http://stackoverflow.com/questions/12245935/is-task-factory-startnew-guaranteed-to-use-another-thread-than-the-calling-thr
                // this idiom is dependent upon the a TaskScheduler not implementing the
                // override QueueTask as task inlining (as opposed to queueing). this seems 
                // implausible to the author, since none of the .NET schedulers do this and
                // it is considered bad form (the OrleansTaskScheduler does not do this).
                //
                // if, for some reason this doesn't hold true, we can guarantee what we
                // want by passing a placeholder continuation token into Task.StartNew() 
                // instead. i.e.:
                //
                // return Task.StartNew(() => ..., new CancellationToken()); 
                Func<Task> asyncFunc =
                    async () =>
                        await this.LocalObjectMessagePumpAsync(objectData);
                Task.Run(asyncFunc).Ignore();
            }
        }

        private async Task LocalObjectMessagePumpAsync(LocalObjectData objectData)
        {
            while (true)
            {
                try
                {
                    Message message;
                    lock (objectData.Messages)
                    {
                        if (objectData.Messages.Count == 0)
                        {
                            objectData.Running = false;
                            break;
                        }
                        message = objectData.Messages.Dequeue();
                    }

                    if (ExpireMessageIfExpired(message, MessagingStatisticsGroup.Phase.Invoke))
                        continue;

                    RequestContext.Import(message.RequestContextData);
                    var request = (InvokeMethodRequest)message.BodyObject;
                    var targetOb = (IAddressable)objectData.LocalObject.Target;
                    object resultObject = null;
                    Exception caught = null;
                    try
                    {
                        // exceptions thrown within this scope are not considered to be thrown from user code
                        // and not from runtime code.
                        var resultPromise = objectData.Invoker.Invoke(
                            targetOb,
                            request.InterfaceId,
                            request.MethodId,
                            request.Arguments);
                        if (resultPromise != null) // it will be null for one way messages
                        {
                            resultObject = await resultPromise;
                        }
                    }
                    catch (Exception exc)
                    {
                        // the exception needs to be reported in the log or propagated back to the caller.
                        caught = exc;
                    }
                    if (caught != null)
                        this.ReportException(message, caught);
                    else if (message.Direction != Message.Directions.OneWay)
                        await this.SendResponseAsync(message, resultObject);
                }catch(Exception)
                {
                    // ignore, keep looping.
                }
            }
        }

        private static bool ExpireMessageIfExpired(Message message, MessagingStatisticsGroup.Phase phase)
        {
            if (message.IsExpired)
            {
                message.DropExpiredMessage(phase);
                return true;
            }
                return false;
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
        private Task 
            SendResponseAsync(
                Message message,
                object resultObject)
        {
            if (ExpireMessageIfExpired(message, MessagingStatisticsGroup.Phase.Respond))
                return TaskDone.Done;

            object deepCopy = null;
            try
            {
                // we're expected to notify the caller if the deep copy failed.
                deepCopy = SerializationManager.DeepCopy(resultObject);
            }
            catch (Exception exc2)
            {
                SendResponse(message, Response.ExceptionResponse(exc2));
                logger.Warn(
                    ErrorCode.ProxyClient_OGC_SendResponseFailed,
                    "Exception trying to send a response.", exc2);
                return TaskDone.Done;
            }

            // the deep-copy succeeded.
            SendResponse(message, new Response(deepCopy));
            return TaskDone.Done;
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
        private void ReportException(Message message, Exception exception)
        {
            var request = (InvokeMethodRequest)message.BodyObject;
            switch (message.Direction)
            {
                default:
                    throw new InvalidOperationException();
                case Message.Directions.OneWay:
                    {
                        logger.Error(
                            ErrorCode.ProxyClient_OGC_UnhandledExceptionInOneWayInvoke,
                            String.Format(
                                "Exception during invocation of notification method {0}, interface {1}. Ignoring exception because this is a one way request.", 
                                request.MethodId, 
                                request.InterfaceId), 
                            exception);
                        break;
                    }
                case Message.Directions.Request:
                    {
                        Exception deepCopy = null;
                        try
                        {
                            // we're expected to notify the caller if the deep copy failed.
                            deepCopy = (Exception)SerializationManager.DeepCopy(exception);
                        }
                        catch (Exception ex2)
                        {
                            SendResponse(message, Response.ExceptionResponse(ex2));
                            logger.Warn(
                                ErrorCode.ProxyClient_OGC_SendExceptionResponseFailed,
                                "Exception trying to send an exception response", ex2);
                            return;
                        }
                        // the deep-copy succeeded.
                        var response = Response.ExceptionResponse(deepCopy);
                        SendResponse(message, response);
                        break;
                    }
            }
        }

        private void SendResponse(Message request, Response response)
        {
            var message = request.CreateResponseMessage();
            message.BodyObject = response;

            transport.SendMessage(message);
        }

        /// <summary>
        /// For testing only.
        /// </summary>
        public void Disconnect()
        {
            transport.Disconnect();
        }

        /// <summary>
        /// For testing only.
        /// </summary>
        public void Reconnect()
        {
            transport.Reconnect();
        }

        #region Implementation of IRuntimeClient

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Reliability", "CA2000:Dispose objects before losing scope",
            Justification = "CallbackData is IDisposable but instances exist beyond lifetime of this method so cannot Dispose yet.")]
        public void SendRequest(GrainReference target, InvokeMethodRequest request, TaskCompletionSource<object> context, Action<Message, TaskCompletionSource<object>> callback, string debugContext = null, InvokeMethodOptions options = InvokeMethodOptions.None, string genericArguments = null)
        {
            var message = Message.CreateMessage(request, options);
            SendRequestMessage(target, message, context, callback, debugContext, options, genericArguments);
        }

        private void SendRequestMessage(GrainReference target, Message message, TaskCompletionSource<object> context, Action<Message, TaskCompletionSource<object>> callback, string debugContext = null, InvokeMethodOptions options = InvokeMethodOptions.None, string genericArguments = null)
        {
            var targetGrainId = target.GrainId;
            var oneWay = (options & InvokeMethodOptions.OneWay) != 0;
            message.SendingGrain = CurrentActivationAddress.Grain;
            message.SendingActivation = CurrentActivationAddress.Activation;
            message.TargetGrain = targetGrainId;
            if (!String.IsNullOrEmpty(genericArguments))
                message.GenericGrainType = genericArguments;

            if (targetGrainId.IsSystemTarget)
            {
                // If the silo isn't be supplied, it will be filled in by the sender to be the gateway silo
                message.TargetSilo = target.SystemTargetSilo;
                if (target.SystemTargetSilo != null)
                {
                    message.TargetActivation = ActivationId.GetSystemActivation(targetGrainId, target.SystemTargetSilo);
                }
            }
            // Client sending messages to another client (observer). Yes, we support that.
            if (target.IsObserverReference)
            {
                message.TargetObserverId = target.ObserverId;
            }
            
            if (debugContext != null)
            {
                message.DebugContext = debugContext;
            }
            if (message.IsExpirableMessage(config))
            {
                // don't set expiration for system target messages.
                message.Expiration = DateTime.UtcNow + responseTimeout + Constants.MAXIMUM_CLOCK_SKEW;
            }

            if (!oneWay)
            {
                var callbackData = new CallbackData(callback, TryResendMessage, context, message, () => UnRegisterCallback(message.Id), config);
                callbacks.TryAdd(message.Id, callbackData);
                callbackData.StartTimer(responseTimeout);
            }

            if (logger.IsVerbose2) logger.Verbose2("Send {0}", message);
            transport.SendMessage(message);
        }


        private bool TryResendMessage(Message message)
        {
            if (!message.MayResend(config))
            {
                return false;
            }

            if (logger.IsVerbose) logger.Verbose("Resend {0}", message);

            message.ResendCount = message.ResendCount + 1;
            message.SetMetadata(Message.Metadata.TARGET_HISTORY, message.GetTargetHistory());
            
            if (!message.TargetGrain.IsSystemTarget)
            {
                message.RemoveHeader(Message.Header.TARGET_ACTIVATION);
                message.RemoveHeader(Message.Header.TARGET_SILO);
            }
            transport.SendMessage(message);
            return true;
        }

        public void ReceiveResponse(Message response)
        {
            if (logger.IsVerbose2) logger.Verbose2("Received {0}", response);

            // ignore duplicate requests
            if (response.Result == Message.ResponseTypes.Rejection && response.RejectionType == Message.RejectionTypes.DuplicateRequest)
                return;

            CallbackData callbackData;
            var found = callbacks.TryGetValue(response.Id, out callbackData);
            if (found)
            {
                // We need to import the RequestContext here as well.
                // Unfortunately, it is not enough, since CallContext.LogicalGetData will not flow "up" from task completion source into the resolved task.
                // RequestContext.Import(response.RequestContextData);
                callbackData.DoCallback(response);
            }
            else
            {
                logger.Warn(ErrorCode.Runtime_Error_100011, "No callback for response message: " + response);
            }
        }

        private void UnRegisterCallback(CorrelationId id)
        {
            CallbackData ignore;
            callbacks.TryRemove(id, out ignore);
        }

        public void Reset()
        {
            Utils.SafeExecute(() =>
            {
                if (logger != null)
                {
                    logger.Info("OutsideRuntimeClient.Reset(): client Id " + clientId);
                }
            });

            Utils.SafeExecute(() =>
            {
                if (clientProviderRuntime != null)
                {
                    clientProviderRuntime.Reset().WaitWithThrow(resetTimeout);
                }
            }, logger, "Client.clientProviderRuntime.Reset");
            Utils.SafeExecute(() =>
            {
                if (StatisticsCollector.CollectThreadTimeTrackingStats)
                {
                    incomingMessagesThreadTimeTracking.OnStopExecution();
                }
            }, logger, "Client.incomingMessagesThreadTimeTracking.OnStopExecution");
            Utils.SafeExecute(() =>
            {
                if (transport != null)
                {
                    transport.PrepareToStop();
                }
            }, logger, "Client.PrepareToStop-Transport");

            listenForMessages = false;
            Utils.SafeExecute(() =>
            {
                if (listeningCts != null)
                {
                    listeningCts.Cancel();
                }
            }, logger, "Client.Stop-ListeningCTS");
        Utils.SafeExecute(() =>
            {
                if (transport != null)
                {
                    transport.Stop();
                }
            }, logger, "Client.Stop-Transport");
            Utils.SafeExecute(() =>
            {
                if (ClientStatistics != null)
                {
                    ClientStatistics.Stop();
                }
            }, logger, "Client.Stop-ClientStatistics");
            ConstructorReset();
        }

        private void ConstructorReset()
        {
            Utils.SafeExecute(() =>
            {
                if (logger != null)
                {
                    logger.Info("OutsideRuntimeClient.ConstructorReset(): client Id " + clientId);
                }
            });
            
            try
            {
                UnobservedExceptionsHandlerClass.ResetUnobservedExceptionHandler();
            }
            catch (Exception) { }
            try
            {
                AppDomain.CurrentDomain.DomainUnload -= CurrentDomain_DomainUnload;
            }
            catch (Exception) { }
            try
            {
                if (clientProviderRuntime != null)
                {
                    clientProviderRuntime.Reset().WaitWithThrow(resetTimeout);
                }
            }
            catch (Exception) { }
            try
            {
                TraceLogger.UnInitialize();
            }
            catch (Exception) { }
        }

        public void SetResponseTimeout(TimeSpan timeout)
        {
            responseTimeout = timeout;
        }
        public TimeSpan GetResponseTimeout()
        {
            return responseTimeout;
        }

        public Task<IGrainReminder> RegisterOrUpdateReminder(string reminderName, TimeSpan dueTime, TimeSpan period)
        {
            throw new InvalidOperationException("RegisterReminder can only be called from inside a grain");
        }

        public Task UnregisterReminder(IGrainReminder reminder)
        {
            throw new InvalidOperationException("UnregisterReminder can only be called from inside a grain");
        }

        public Task<IGrainReminder> GetReminder(string reminderName)
        {
            throw new InvalidOperationException("GetReminder can only be called from inside a grain");
        }

        public Task<List<IGrainReminder>> GetReminders()
        {
            throw new InvalidOperationException("GetReminders can only be called from inside a grain");
        }

        public SiloStatus GetSiloStatus(SiloAddress silo)
        {
            throw new InvalidOperationException("GetSiloStatus can only be called on the silo.");
        }

        public async Task ExecAsync(Func<Task> asyncFunction, ISchedulingContext context, string activityName)
        {
            await Task.Run(asyncFunction); // No grain context on client - run on .NET thread pool
        }

        public GrainReference CreateObjectReference(IAddressable obj, IGrainMethodInvoker invoker)
        {
            if (obj is GrainReference)
                throw new ArgumentException("Argument obj is already a grain reference.");

            GrainReference gr = GrainReference.NewObserverGrainReference(clientId, GuidId.GetNewGuidId());
            if (!localObjects.TryAdd(gr.ObserverId, new LocalObjectData(obj, gr.ObserverId, invoker)))
            {
                throw new ArgumentException(String.Format("Failed to add new observer {0} to localObjects collection.", gr), "gr");
            }
            return gr;
        }

        public void DeleteObjectReference(IAddressable obj)
        {
            if (!(obj is GrainReference))
                throw new ArgumentException("Argument reference is not a grain reference.");

            var reference = (GrainReference) obj;
            LocalObjectData ignore;
            if (!localObjects.TryRemove(reference.ObserverId, out ignore))
                throw new ArgumentException("Reference is not associated with a local object.", "reference");
        }

        public void DeactivateOnIdle(ActivationId id)
        {
            throw new InvalidOperationException();
        }

        #endregion

        private void CurrentDomain_DomainUnload(object sender, EventArgs e)
        {
            try
            {
                logger.Warn(ErrorCode.ProxyClient_AppDomain_Unload, 
                    String.Format("Current AppDomain={0} is unloading.", PrintAppDomainDetails()));
                TraceLogger.Flush();
            }
            catch(Exception)
            {
                // just ignore, make sure not to throw from here.
            }
        }
        private string PrintAppDomainDetails()
        {
            return string.Format("<AppDomain.Id={0}, AppDomain.FriendlyName={1}>", AppDomain.CurrentDomain.Id, AppDomain.CurrentDomain.FriendlyName);
        }


        private class LocalObjectData
        {
            internal WeakReference LocalObject { get; private set; }
            internal IGrainMethodInvoker Invoker { get; private set; }
            internal GuidId ObserverId { get; private set; }
            internal Queue<Message> Messages { get; private set; }
            internal bool Running { get; set; }

            internal LocalObjectData(IAddressable obj, GuidId observerId, IGrainMethodInvoker invoker)
            {
                LocalObject = new WeakReference(obj);
                ObserverId = observerId;
                Invoker = invoker;
                Messages = new Queue<Message>();
                Running = false;
            }
        }

        public void Dispose()
        {
            if (listeningCts != null)
            {
                listeningCts.Dispose();
                listeningCts = null;
            }

            GC.SuppressFinalize(this);
        }


        public IGrainTypeResolver GrainTypeResolver
        {
            get { return grainInterfaceMap; }
        }

        public string CaptureRuntimeEnvironment()
        {
            throw new NotImplementedException();
        }

        public IGrainMethodInvoker GetInvoker(int interfaceId, string genericGrainType = null)
        {
            throw new NotImplementedException();
        }

        public void BreakOutstandingMessagesToDeadSilo(SiloAddress deadSilo)
        {
            foreach (var callback in callbacks)
            {
                if (deadSilo.Equals(callback.Value.Message.TargetSilo))
                {
                    callback.Value.OnTargetSiloFail();
                }
            }
        }
    }
}
