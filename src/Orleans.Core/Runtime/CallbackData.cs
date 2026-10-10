using System;
using System.Threading;
using Microsoft.Extensions.Logging;
using Orleans.Serialization.Invocation;

namespace Orleans.Runtime
{
    internal sealed partial class CallbackData
    {
        private const int StateNone = 0;
        private const int StateCompleted = 1;
        private const int StateCancellationRegistrationPending = 2;
        private const int StateCancellationRegistrationPublished = 4;
        private const int StateRouteUpdateReceived = 8;
        private const int StateCancellationRequested = 16;
        private const int StateCancellable = 32;

        private readonly SharedCallbackData shared;
        private readonly IResponseCompletionSource context;
        private readonly ApplicationRequestInstruments _applicationRequestInstruments;
        private readonly long _startTimestamp;
        private int _state;
        private StatusResponse? lastKnownStatus;
        private CancellationTokenRegistration _cancellationTokenRegistration;
        private readonly TimeSpan _responseTimeout;
        private readonly long _responseTimeoutTicks;

        public CallbackData(
            SharedCallbackData shared,
            IResponseCompletionSource ctx,
            Message msg,
            ApplicationRequestInstruments applicationRequestInstruments)
        {
            this.shared = shared;
            this.context = ctx;
            _startTimestamp = shared.TimeProvider.GetTimestamp();
            var invokable = msg.BodyObject as IInvokable;
            _responseTimeout = invokable?.GetDefaultResponseTimeout() ?? shared.ResponseTimeout;
            _responseTimeoutTicks = shared.GetTimestampTicks(_responseTimeout);
            _state = invokable?.IsCancellable == true ? StateCancellable : StateNone;
            this.Message = msg;
            _applicationRequestInstruments = applicationRequestInstruments;
        }

        public Message Message { get; } // might hold metadata used by response pipeline

        public bool IsCompleted => (Volatile.Read(ref _state) & StateCompleted) != 0;

        public void SubscribeForCancellation(CancellationToken cancellationToken)
        {
            if (!cancellationToken.CanBeCanceled)
            {
                return;
            }

            lock (Message)
            {
                if ((_state & (StateCompleted | StateCancellationRegistrationPending | StateCancellationRegistrationPublished)) != StateNone)
                {
                    return;
                }

                _state |= StateCancellationRegistrationPending;
            }

            var registration = cancellationToken.UnsafeRegister(static (arg, token) =>
            {
                var callbackData = (CallbackData)arg!;
                callbackData.OnCancellation(token);
            }, this);

            lock (Message)
            {
                if (!IsCompleted)
                {
                    _cancellationTokenRegistration = registration;
                    _state = (_state & ~StateCancellationRegistrationPending) | StateCancellationRegistrationPublished;
                    return;
                }
            }

            registration.Dispose();
        }

        private void SignalCancellation()
        {
            if ((Volatile.Read(ref _state) & StateCancellable) != 0)
            {
                shared.CancellationManager?.SignalCancellation(Message.TargetSilo, Message.TargetGrain, Message.SendingGrain, Message.Id);
            }
        }

        public bool OnStatusUpdate(StatusResponse status, int forwardingGeneration)
        {
            lock (Message)
            {
                if (IsCompleted)
                {
                    return false;
                }

                if (status.IsRouteUpdate)
                {
                    if ((_state & StateRouteUpdateReceived) != 0 && forwardingGeneration <= Message.ForwardCount)
                    {
                        return false;
                    }

                    Message.ForwardCount = forwardingGeneration;
                    Message.TargetSilo = status.ForwardedTo;
                    _state |= StateRouteUpdateReceived;
                    if ((_state & StateCancellationRequested) != 0)
                    {
                        SignalCancellation();
                    }
                }
                else
                {
                    this.lastKnownStatus = status;
                }

                return true;
            }
        }

        public bool IsExpired(long currentTimestamp)
        {
            var duration = currentTimestamp - _startTimestamp;
            return duration > _responseTimeoutTicks;
        }

        private string GetTargetGrainType()
        {
            var type = Message.TargetGrain.Type;
            return type.IsDefault ? "unknown" : type.ToString()!;
        }

        private void OnCancellation(CancellationToken cancellationToken)
        {
            lock (Message)
            {
                _state |= StateCancellationRequested;
            }
            // If waiting for acknowledgement is enabled, simply signal to the remote grain that cancellation
            // is requested and return.
            if (shared.WaitForCancellationAcknowledgement)
            {
                SignalCancellation();
                return;
            }

            // Otherwise, cancel the request immediately, without waiting for the callee to acknowledge the
            // cancellation request. The callee will still be signaled.
            if (!TryComplete())
            {
                return;
            }

            RecordElapsedTime();
            SignalCancellation();
            shared.Unregister(Message);
            _applicationRequestInstruments.OnAppRequestsCanceled(GetTargetGrainType());
            OrleansCallBackDataEvent.Instance.OnCanceled(Message);
            context.Complete(Response.FromException(new OperationCanceledException(cancellationToken)));
            DisposeCancellationRegistration();
        }

        public void OnTimeout()
        {
            if (!TryComplete())
            {
                return;
            }

            RecordElapsedTime();
            if (shared.CancelRequestOnTimeout)
            {
                SignalCancellation();
            }

            this.shared.Unregister(this.Message);
            DisposeCancellationRegistration();
            _applicationRequestInstruments.OnAppRequestsTimedOut(GetTargetGrainType());

            OrleansCallBackDataEvent.Instance.OnTimeout(this.Message);

            var msg = this.Message; // Local working copy

            var statusMessage = lastKnownStatus is StatusResponse status ? $"Last known status is {status}. " : string.Empty;
            var timeout = _responseTimeout;
            LogTimeout(this.shared.Logger, timeout, msg, statusMessage);

            var exception = new TimeoutException($"Response did not arrive on time in {timeout} for message: {msg}. {statusMessage}");
            context.Complete(Response.FromException(exception));
        }

        public void OnTargetSiloFail()
        {
            if (Message.IsRelocatableRequest)
            {
                return;
            }

            if (!TryComplete())
            {
                return;
            }

            RecordElapsedTime();
            this.shared.Unregister(this.Message);
            DisposeCancellationRegistration();

            OrleansCallBackDataEvent.Instance.OnTargetSiloFail(this.Message);
            var msg = this.Message;
            var statusMessage = lastKnownStatus is StatusResponse status ? $"Last known status is {status}. " : string.Empty;
            LogTargetSiloFail(this.shared.Logger, msg, statusMessage, Constants.TroubleshootingHelpLink);
            var exception = new SiloUnavailableException($"The target silo became unavailable for message: {msg}. {statusMessage}See {Constants.TroubleshootingHelpLink} for troubleshooting help.");
            this.context.Complete(Response.FromException(exception));
        }

        public void OnHostShutdown()
        {
            if (!TryComplete())
            {
                return;
            }

            RecordElapsedTime();
            this.shared.Unregister(this.Message);
            DisposeCancellationRegistration();

            var msg = this.Message;
            var exception = new SiloUnavailableException($"The local Orleans host is shutting down and can no longer process the request: {msg}.");
            this.context.Complete(Response.FromException(exception));
        }

        public void DoCallback(Message response)
        {
            if (!TryComplete())
            {
                response.Dispose();
                return;
            }

            OrleansCallBackDataEvent.Instance.DoCallback(this.Message);

            RecordElapsedTime();
            DisposeCancellationRegistration();

            ResponseCallback(response, this.context);
            response.Dispose();
        }

        private bool TryComplete()
        {
            lock (Message)
            {
                if (IsCompleted)
                {
                    return false;
                }

                _state |= StateCompleted;
                return true;
            }
        }

        private void RecordElapsedTime()
        {
            if (_applicationRequestInstruments.AppRequestsLatencyEnabled)
            {
                var elapsedMilliseconds = shared.TimeProvider.GetElapsedTime(_startTimestamp).TotalMilliseconds;
                _applicationRequestInstruments.OnAppRequestsEnd(elapsedMilliseconds);
            }
        }

        private void DisposeCancellationRegistration()
        {
            // If registration is still pending, its publisher observes completion and disposes it.
            if ((Volatile.Read(ref _state) & StateCancellationRegistrationPublished) != 0)
            {
                _cancellationTokenRegistration.Dispose();
            }
        }

        private static void ResponseCallback(Message message, IResponseCompletionSource context)
        {
            try
            {
                var body = message.BodyObject;
                if (body is Response response)
                {
                    context.Complete(response);
                }
                else
                {
                    HandleRejectionResponse(context, body as RejectionResponse);
                }
            }
            catch (Exception exc)
            {
                // catch the exception and break the promise with it.
                context.Complete(Response.FromException(exc));
            }

            static void HandleRejectionResponse(IResponseCompletionSource context, RejectionResponse? rejection)
            {
                Exception exception;
                if (rejection?.RejectionType is Message.RejectionTypes.GatewayTooBusy)
                {
                    exception = new GatewayTooBusyException();
                }
                else
                {
                    exception = rejection?.Exception ?? new OrleansMessageRejectionException(rejection?.RejectionInfo ?? "Unable to send request - no rejection info available");
                }

                context.Complete(Response.FromException(exception));
            }
        }

        [LoggerMessage(
            EventId = (int)ErrorCode.Runtime_Error_100157,
            Level = LogLevel.Warning,
            Message = "Response did not arrive on time in '{Timeout}' for message: '{Message}'. {StatusMessage}About to break its promise."
        )]
        private static partial void LogTimeout(ILogger logger, TimeSpan timeout, Message message, string statusMessage);

        [LoggerMessage(
            EventId = (int)ErrorCode.Runtime_Error_100157,
            Level = LogLevel.Warning,
            Message = "The target silo became unavailable for message: '{Message}'. {StatusMessage}See {TroubleshootingHelpLink} for troubleshooting help. About to break its promise."
        )]
        private static partial void LogTargetSiloFail(ILogger logger, Message message, string statusMessage, string troubleshootingHelpLink);
    }
}
