using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.CodeGeneration;
using Orleans.Internal;
using Orleans.Runtime.Messaging;
using Orleans.Streams;

namespace Orleans.Runtime
{
    /// <summary>
    /// A client which is hosted within a silo.
    /// </summary>
    internal sealed class HostedClient : IDisposable, ILifecycleParticipant<ISiloLifecycle>
    {
        private readonly Channel<Message> incomingMessages;
        private readonly Dictionary<Type, Tuple<IGrainExtension, IAddressable>> extensionsTable = new Dictionary<Type, Tuple<IGrainExtension, IAddressable>>();
        private readonly AsyncLock lockable = new AsyncLock();
        private readonly IGrainReferenceRuntime grainReferenceRuntime;
        private readonly InvokableObjectManager invokableObjects;
        private readonly IRuntimeClient runtimeClient;
        private readonly ClientObserverRegistrar clientObserverRegistrar;
        private readonly ILogger logger;
        private readonly IInternalGrainFactory grainFactory;
        private readonly MessageCenter siloMessageCenter;
        private readonly MessagingTrace messagingTrace;
        private bool disposing;
        private Task messagePump;

        public HostedClient(
            IRuntimeClient runtimeClient,
            ClientObserverRegistrar clientObserverRegistrar,
            ILocalSiloDetails siloDetails,
            ILogger<HostedClient> logger,
            IGrainReferenceRuntime grainReferenceRuntime,
            IInternalGrainFactory grainFactory,
            InvokableObjectManager invokableObjectManager,
            MessageCenter messageCenter,
            MessagingTrace messagingTrace)
        {
            this.incomingMessages = Channel.CreateUnbounded<Message>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
            });

            this.runtimeClient = runtimeClient;
            this.clientObserverRegistrar = clientObserverRegistrar;
            this.grainReferenceRuntime = grainReferenceRuntime;
            this.grainFactory = grainFactory;
            this.invokableObjects = invokableObjectManager;
            this.siloMessageCenter = messageCenter;
            this.messagingTrace = messagingTrace;
            this.logger = logger;

            this.ClientId = ClientGrainId.Create($"hosted-{messageCenter.MyAddress.ToParsableString()}");
            this.ClientAddress = ActivationAddress.NewActivationAddress(siloDetails.SiloAddress, this.ClientId.GrainId);
        }

        /// <inheritdoc />
        public ActivationAddress ClientAddress { get; }

        /// <inheritdoc />
        public ClientGrainId ClientId { get; }

        /// <inheritdoc />
        public StreamDirectory StreamDirectory { get; } = new StreamDirectory();
        
        /// <inheritdoc />
        public override string ToString() => $"{nameof(HostedClient)}_{this.ClientAddress}";

        /// <inheritdoc />
        public GrainReference CreateObjectReference(IAddressable obj, IGrainMethodInvoker invoker)
        {
            if (obj is GrainReference) throw new ArgumentException("Argument obj is already a grain reference.");

            var observerId = ObserverGrainId.Create(this.ClientId);
            var grainReference = GrainReference.NewObserverGrainReference(observerId, this.grainReferenceRuntime);
            if (!this.invokableObjects.TryRegister(obj, observerId, invoker))
            {
                throw new ArgumentException(
                    string.Format("Failed to add new observer {0} to localObjects collection.", grainReference),
                    nameof(grainReference));
            }

            return grainReference;
        }

        /// <inheritdoc />
        public void DeleteObjectReference(IAddressable obj)
        {
            if (!(obj is GrainReference reference))
            {
                throw new ArgumentException("Argument reference is not a grain reference.");
            }

            if (!ObserverGrainId.TryParse(reference.GrainId, out var observerId))
            {
                throw new ArgumentException($"Reference {reference.GrainId} is not an observer reference");
            }

            if (!invokableObjects.TryDeregister(observerId))
            {
                throw new ArgumentException("Reference is not associated with a local object.", "reference");
            }
        }

        /// <inheritdoc />
        public async Task<Tuple<TExtension, TExtensionInterface>> BindExtension<TExtension, TExtensionInterface>(Func<TExtension> newExtensionFunc)
            where TExtension : IGrainExtension
            where TExtensionInterface : IGrainExtension
        {
            IAddressable addressable;
            TExtension extension;

            using (await this.lockable.LockAsync())
            {
                if (this.extensionsTable.TryGetValue(typeof(TExtensionInterface), out var entry))
                {
                    extension = (TExtension) entry.Item1;
                    addressable = entry.Item2;
                }
                else
                {
                    extension = newExtensionFunc();
                    var obj = this.grainFactory.CreateObjectReference<TExtensionInterface>(extension);

                    addressable = obj;

                    if (null == addressable)
                    {
                        throw new NullReferenceException("addressable");
                    }
                    entry = Tuple.Create((IGrainExtension) extension, addressable);
                    this.extensionsTable.Add(typeof(TExtensionInterface), entry);
                }
            }

            var typedAddressable = addressable.Cast<TExtensionInterface>();
            // we have to return the extension as well as the IAddressable because the caller needs to root the extension
            // to prevent it from being collected (the IAddressable uses a weak reference).
            return Tuple.Create(extension, typedAddressable);
        }

        /// <inheritdoc />
        public bool TryDispatchToClient(Message message)
        {
            if (!ClientGrainId.TryParse(message.TargetGrain, out var targetClient) || !this.ClientId.Equals(targetClient))
            {
                return false;
            }

            if (message.IsExpired)
            {
                this.messagingTrace.OnDropExpiredMessage(message, MessagingStatisticsGroup.Phase.Receive);
                return true;
            }

            if (message.Direction == Message.Directions.Response)
            {
                // Requests are made through the runtime client, so deliver responses to the rutnime client so that the request callback can be executed.
                this.runtimeClient.ReceiveResponse(message);
            }
            else
            {
                // Requests agrainst client objects are scheduled for execution on the client.
                this.incomingMessages.Writer.TryWrite(message);
            }

            return true;
        }

        /// <inheritdoc />
        void IDisposable.Dispose()
        {
            if (this.disposing) return;
            this.disposing = true;
            Utils.SafeExecute(() => this.clientObserverRegistrar.ClientDropped(this.ClientId));
            Utils.SafeExecute(() => this.clientObserverRegistrar.SetHostedClient(null));
            Utils.SafeExecute(() => this.siloMessageCenter.SetHostedClient(null));
            Utils.SafeExecute(() => this.incomingMessages.Writer.TryComplete());
            Utils.SafeExecute(() => this.messagePump?.GetAwaiter().GetResult());
        }

        private void Start()
        {
            this.messagePump = Task.Run(this.RunClientMessagePump);
        }

        private async Task RunClientMessagePump()
        {
            var reader = this.incomingMessages.Reader;
            while (true)
            {
                try
                {
                    var more = await reader.WaitToReadAsync();
                    if (!more)
                    {
                        this.logger.LogInformation($"{nameof(HostedClient)} completed processing all messages. Shutting down.");
                        break;
                    }

                    while (reader.TryRead(out var message))
                    {
                        if (message == null) continue;
                        switch (message.Direction)
                        {
                            case Message.Directions.OneWay:
                            case Message.Directions.Request:
                                this.invokableObjects.Dispatch(message);
                                break;
                            default:
                                this.logger.LogError((int)ErrorCode.Runtime_Error_100327, "Message not supported: {Message}", message);
                                break;
                        }
                    }
                }
                catch (Exception exception)
                {
                    this.logger.LogError((int)ErrorCode.Runtime_Error_100326, "RunClientMessagePump has thrown an exception: {Exception}. Continuing.", exception);
                }
            }
        }

        void ILifecycleParticipant<ISiloLifecycle>.Participate(ISiloLifecycle lifecycle)
        {
            lifecycle.Subscribe("HostedClient", ServiceLifecycleStage.RuntimeGrainServices, OnStart, OnStop);

            Task OnStart(CancellationToken cancellation)
            {
                if (cancellation.IsCancellationRequested) return Task.CompletedTask;

                // Register with the directory and message center so that we can receive messages.
                this.clientObserverRegistrar.SetHostedClient(this);
                this.clientObserverRegistrar.ClientAdded(this.ClientId);
                this.siloMessageCenter.SetHostedClient(this);

                // Start pumping messages.
                this.Start();

                var clusterClient = this.runtimeClient.ServiceProvider.GetRequiredService<IClusterClient>();
                return clusterClient.Connect();
            }

            async Task OnStop(CancellationToken cancellation)
            {
                this.incomingMessages.Writer.TryComplete();
                
                if (this.messagePump != null)
                {
                    await Task.WhenAny(cancellation.WhenCancelled(), this.messagePump);
                }
                
                if (cancellation.IsCancellationRequested) return;

                var clusterClient = this.runtimeClient.ServiceProvider.GetRequiredService<IClusterClient>();
                await clusterClient.Close();
            }
        }
    }
}