using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.CodeGeneration;
using Orleans.Runtime.Messaging;
using Orleans.Streams;

namespace Orleans.Runtime
{
    /// <summary>
    /// A client which is hosted within a silo.
    /// </summary>
    internal sealed class HostedClient : IDisposable, IHostedClient
    {
        private readonly BlockingCollection<Message> incomingMessages = new BlockingCollection<Message>();
        private readonly CancellationTokenSource listeningCts = new CancellationTokenSource();
        private readonly Dictionary<Type, Tuple<IGrainExtension, IAddressable>> extensionsTable = new Dictionary<Type, Tuple<IGrainExtension, IAddressable>>();
        private readonly AsyncLock lockable = new AsyncLock();
        private readonly IGrainReferenceRuntime grainReferenceRuntime;
        private readonly InvokableObjectManager invokableObjects;
        private readonly IRuntimeClient runtimeClient;
        private readonly ClientObserverRegistrar clientObserverRegistrar;
        private readonly ILogger logger;
        private readonly IInternalGrainFactory grainFactory;
        private readonly ISiloMessageCenter siloMessageCenter;
        private bool disposing;
        private Thread messagePump;

        public HostedClient(
            IRuntimeClient runtimeClient,
            ClientObserverRegistrar clientObserverRegistrar,
            ILocalSiloDetails siloDetails,
            ILogger<HostedClient> logger,
            IGrainReferenceRuntime grainReferenceRuntime,
            IInternalGrainFactory grainFactory,
            InvokableObjectManager invokableObjectManager,
            ISiloMessageCenter messageCenter)
        {
            this.runtimeClient = runtimeClient;
            this.clientObserverRegistrar = clientObserverRegistrar;
            this.grainReferenceRuntime = grainReferenceRuntime;
            this.grainFactory = grainFactory;
            this.invokableObjects = invokableObjectManager;
            this.siloMessageCenter = messageCenter;
            this.logger = logger;

            this.ClientAddress = ActivationAddress.NewActivationAddress(siloDetails.SiloAddress, GrainId.NewClientId());

            // Register with the directory and message center so that we can receive messages.
            this.clientObserverRegistrar.SetHostedClient(this);
            this.clientObserverRegistrar.ClientAdded(this.ClientId);
            this.siloMessageCenter.SetHostedClient(this);

            // Start pumping messages.
            this.Start();
        }

        /// <inheritdoc />
        public ActivationAddress ClientAddress { get; }

        /// <inheritdoc />
        public GrainId ClientId => this.ClientAddress.Grain;

        /// <inheritdoc />
        public StreamDirectory StreamDirectory { get; } = new StreamDirectory();
        
        /// <inheritdoc />
        public override string ToString() => $"{nameof(HostedClient)}_{this.ClientAddress}";

        /// <inheritdoc />
        public GrainReference CreateObjectReference(IAddressable obj, IGrainMethodInvoker invoker)
        {
            if (obj is GrainReference) throw new ArgumentException("Argument obj is already a grain reference.");

            var grainReference = GrainReference.NewObserverGrainReference(this.ClientAddress.Grain, GuidId.GetNewGuidId(), this.grainReferenceRuntime);
            if (!this.invokableObjects.TryRegister(obj, grainReference.ObserverId, invoker))
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
            if (!(obj is GrainReference reference)) throw new ArgumentException("Argument reference is not a grain reference.");

            if (!this.invokableObjects.TryDeregister(reference.ObserverId))throw new ArgumentException("Reference is not associated with a local object.", nameof(obj));
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
            if (!this.ClientId.Equals(message.TargetGrain)) return false;
            if (message.IsExpired)
            {
                message.DropExpiredMessage(MessagingStatisticsGroup.Phase.Receive);
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
                this.incomingMessages.Add(message);
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
            Utils.SafeExecute(() => this.listeningCts.Cancel(false));
            Utils.SafeExecute(() => this.listeningCts.Dispose());
            Utils.SafeExecute(() => this.messagePump?.Join());
        }

        private void Start()
        {
            this.messagePump = new Thread(_ => this.RunClientMessagePump())
            {
                Name = nameof(HostedClient) + "." + nameof(RunClientMessagePump),
                IsBackground = true,
            };
            this.messagePump.Start();
        }

        private void RunClientMessagePump()
        {
            while (!this.listeningCts.IsCancellationRequested)
            {
                try
                {
                    var message = this.incomingMessages.Take(this.listeningCts.Token);
                    if (message == null) continue;
                    switch (message.Direction)
                    {
                        case Message.Directions.OneWay:
                        case Message.Directions.Request:
                            this.invokableObjects.Dispatch(message);
                            break;
                        default:
                            this.logger.Error(ErrorCode.Runtime_Error_100327, string.Format("Message not supported: {0}.", message));
                            break;
                    }
                }
                catch (Exception exc)
                {
                    this.logger.Error(ErrorCode.Runtime_Error_100326, "RunClientMessagePump has thrown exception", exc);
                }
            }
        }
    }
}