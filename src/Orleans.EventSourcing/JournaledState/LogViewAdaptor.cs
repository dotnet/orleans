using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.EventSourcing.Common;
using Orleans.Journaling;
using Orleans.Storage;

#nullable disable
#pragma warning disable ORLEANSEXP005
namespace Orleans.EventSourcing.JournaledState
{
    /// <summary>
    /// A log view adaptor that persists the event log as journaled state owned by the host grain.
    /// </summary>
    /// <typeparam name="TLogView">Type of log view.</typeparam>
    /// <typeparam name="TLogEntry">Type of log entry.</typeparam>
    internal sealed class LogViewAdaptor<TLogView, TLogEntry> : PrimaryBasedLogViewAdaptor<TLogView, TLogEntry, SubmissionEntry<TLogEntry>>
        where TLogView : class, new()
        where TLogEntry : class
    {
        private const string EventLogStateName = "Orleans.EventSourcing.JournaledState.EventLog";
        private const string WriteVectorStateName = "Orleans.EventSourcing.JournaledState.WriteVector";

        private readonly IJournaledStateManager stateManager;
        private readonly IDurableList<TLogEntry> eventLog;
        private readonly IDurableValue<string> writeVector;

        private Task initializationTask;
        private TLogView confirmedView;
        private int confirmedVersion;

        public LogViewAdaptor(
            ILogViewAdaptorHost<TLogView, TLogEntry> host,
            TLogView initialState,
            ILogConsistencyProtocolServices services)
            : base(host, initialState, services)
        {
            if (host is not IGrainBase grain)
            {
                throw new BadProviderConfigException("The JournaledState log-consistency provider can only be used by grain classes.");
            }

            var serviceProvider = grain.GrainContext.ActivationServices;
            stateManager = serviceProvider.GetRequiredService<IJournaledStateManager>();
            eventLog = serviceProvider.GetRequiredKeyedService<IDurableList<TLogEntry>>(EventLogStateName);
            writeVector = serviceProvider.GetRequiredKeyedService<IDurableValue<string>>(WriteVectorStateName);
        }

        /// <inheritdoc/>
        public override async Task PreOnActivate()
        {
            await EnsureInitializedAsync();
            await base.PreOnActivate();
        }

        /// <inheritdoc/>
        public override Task<IReadOnlyList<TLogEntry>> RetrieveLogSegment(int fromVersion, int toVersion)
        {
            if (fromVersion < 0 || toVersion < fromVersion || toVersion > eventLog.Count)
            {
                throw new ArgumentException("Invalid log segment range.");
            }

            return Task.FromResult<IReadOnlyList<TLogEntry>>(eventLog.Skip(fromVersion).Take(toVersion - fromVersion).ToArray());
        }

        /// <inheritdoc/>
        protected override TLogView LastConfirmedView() => confirmedView;

        /// <inheritdoc/>
        protected override int GetConfirmedVersion() => confirmedVersion;

        /// <inheritdoc/>
        protected override void InitializeConfirmedView(TLogView initialstate)
        {
            confirmedView = initialstate;
            confirmedVersion = 0;
        }

        /// <inheritdoc/>
        protected override async Task ClearPrimaryLogAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await EnsureInitializedAsync();
            eventLog.Clear();
            writeVector.Value = string.Empty;
            await stateManager.WriteStateAsync(cancellationToken);
        }

        /// <inheritdoc/>
        protected override SubmissionEntry<TLogEntry> MakeSubmissionEntry(TLogEntry entry)
        {
            return new SubmissionEntry<TLogEntry> { Entry = entry };
        }

        /// <inheritdoc/>
        protected override async Task ReadAsync()
        {
            enter_operation("ReadAsync");

            while (true)
            {
                try
                {
                    await stateManager.ReadStateAsync(CancellationToken.None);
                    UpdateConfirmedViewFromJournal();

                    Services.Log(LogLevel.Debug, "read success v{0}", confirmedVersion);

                    LastPrimaryIssue.Resolve(Host, Services);
                    break;
                }
                catch (Exception exception)
                {
                    LastPrimaryIssue.Record(new ReadFromJournaledStateFailed { Exception = exception }, Host, Services);
                }

                Services.Log(LogLevel.Debug, "read failed {0}", LastPrimaryIssue);

                await LastPrimaryIssue.DelayBeforeRetry();
            }

            exit_operation("ReadAsync");
        }

        /// <inheritdoc/>
        protected override async Task<int> WriteAsync()
        {
            enter_operation("WriteAsync");

            var updates = GetCurrentBatchOfUpdates();
            if (updates.Length == 0)
            {
                exit_operation("WriteAsync");
                return 0;
            }

            var writeBit = FlipWriteVectorBit();
            foreach (var update in updates)
            {
                eventLog.Add(update.Entry);
            }

            while (true)
            {
                try
                {
                    await stateManager.WriteStateAsync(CancellationToken.None);

                    Services.Log(LogLevel.Debug, "write ({0} updates) success v{1}", updates.Length, eventLog.Count);

                    UpdateConfirmedViewFromJournal();
                    LastPrimaryIssue.Resolve(Host, Services);

                    exit_operation("WriteAsync");
                    return updates.Length;
                }
                catch (Exception exception)
                {
                    LastPrimaryIssue.Record(new UpdateJournaledStateFailed { Exception = exception }, Host, Services);
                }

                Services.Log(LogLevel.Debug, "write failed {0}", LastPrimaryIssue);

                await LastPrimaryIssue.DelayBeforeRetry();
                await RecoverAfterWriteFailureAsync();

                if (writeBit == GetWriteVectorBit())
                {
                    Services.Log(LogLevel.Debug, "last write ({0} updates) was actually a success v{1}", updates.Length, eventLog.Count);

                    UpdateConfirmedViewFromJournal();
                    LastPrimaryIssue.Resolve(Host, Services);

                    exit_operation("WriteAsync");
                    return updates.Length;
                }

                exit_operation("WriteAsync");
                return 0;
            }
        }

        private async Task EnsureInitializedAsync()
        {
            if (initializationTask is null || initializationTask.IsCanceled || initializationTask.IsFaulted)
            {
                initializationTask = stateManager.InitializeAsync(CancellationToken.None).AsTask();
            }

            await initializationTask;
        }

        private async Task RecoverAfterWriteFailureAsync()
        {
            while (true)
            {
                try
                {
                    await stateManager.ReadStateAsync(CancellationToken.None);
                    UpdateConfirmedViewFromJournal();
                    return;
                }
                catch (Exception exception)
                {
                    LastPrimaryIssue.Record(new ReadFromJournaledStateFailed { Exception = exception }, Host, Services);
                }

                Services.Log(LogLevel.Debug, "read failed {0}", LastPrimaryIssue);

                await LastPrimaryIssue.DelayBeforeRetry();
            }
        }

        private void UpdateConfirmedViewFromJournal()
        {
            if (eventLog.Count < confirmedVersion)
            {
                InitializeConfirmedView(InitialState);
            }

            for (var index = confirmedVersion; index < eventLog.Count; index++)
            {
                try
                {
                    Host.UpdateView(confirmedView, eventLog[index]);
                }
                catch (Exception exception)
                {
                    Services.CaughtUserCodeException("UpdateView", nameof(UpdateConfirmedViewFromJournal), exception);
                }
            }

            confirmedVersion = eventLog.Count;
        }

        private bool FlipWriteVectorBit()
        {
            var value = writeVector.Value ?? string.Empty;
            var result = StringEncodedWriteVector.FlipBit(ref value, Services.MyClusterId);
            writeVector.Value = value;
            return result;
        }

        private bool GetWriteVectorBit() => StringEncodedWriteVector.GetBit(writeVector.Value ?? string.Empty, Services.MyClusterId);

        [Serializable]
        [GenerateSerializer]
        public sealed class UpdateJournaledStateFailed : PrimaryOperationFailed
        {
            /// <inheritdoc/>
            public override string ToString()
            {
                return $"write event log to journaled state failed: caught {Exception.GetType().Name}: {Exception.Message}";
            }
        }

        [Serializable]
        [GenerateSerializer]
        public sealed class ReadFromJournaledStateFailed : PrimaryOperationFailed
        {
            /// <inheritdoc/>
            public override string ToString()
            {
                return $"read event log from journaled state failed: caught {Exception.GetType().Name}: {Exception.Message}";
            }
        }

#if DEBUG
        private bool operation_in_progress;
#endif

        [System.Diagnostics.Conditional("DEBUG")]
        private void enter_operation(string name)
        {
#if DEBUG
            Services.Log(LogLevel.Trace, "/-- enter {0}", name);
            System.Diagnostics.Debug.Assert(!operation_in_progress);
            operation_in_progress = true;
#endif
        }

        [System.Diagnostics.Conditional("DEBUG")]
        private void exit_operation(string name)
        {
#if DEBUG
            Services.Log(LogLevel.Trace, "\\-- exit {0}", name);
            System.Diagnostics.Debug.Assert(operation_in_progress);
            operation_in_progress = false;
#endif
        }
    }
}
#pragma warning restore ORLEANSEXP005
