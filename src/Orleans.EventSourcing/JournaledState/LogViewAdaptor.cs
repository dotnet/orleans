using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Concurrency;
using Orleans.EventSourcing.Common;
using Orleans.Journaling;
using Orleans.Storage;

#nullable disable
#pragma warning disable ORLEANSEXP005
namespace Orleans.EventSourcing.JournaledState;

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

    private readonly IGrainBase _grain;
    private readonly IJournaledStateManager _stateManager;
    private readonly IDurableList<TLogEntry> _eventLog;
    private readonly IDurableValue<string> _writeVector;

    private Task _initializationTask;
    private TLogView _confirmedView;
    private int _confirmedVersion;
    private Exception _terminalFailure;

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

        var grainType = grain.GetType();
        var allowsInterleaving = grainType.IsDefined(typeof(ReentrantAttribute), inherit: true)
            || grainType.IsDefined(typeof(MayInterleaveAttribute), inherit: true)
            || grainType.IsDefined(typeof(StatelessWorkerAttribute), inherit: true)
            || grainType.GetInterfaces()
                .SelectMany(static interfaceType => interfaceType.GetMethods())
                .Any(static method => method.IsDefined(typeof(AlwaysInterleaveAttribute), inherit: true));
        if (allowsInterleaving)
        {
            throw new BadProviderConfigException("The JournaledState log-consistency provider requires a single, turn-serialized grain activation.");
        }

        var serviceProvider = grain.GrainContext.ActivationServices;
        _grain = grain;
        _stateManager = serviceProvider.GetRequiredService<IJournaledStateManager>();
        _eventLog = serviceProvider.GetRequiredKeyedService<IDurableList<TLogEntry>>(EventLogStateName);
        _writeVector = serviceProvider.GetRequiredKeyedService<IDurableValue<string>>(WriteVectorStateName);
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
        if (fromVersion < 0 || toVersion < fromVersion || toVersion > _confirmedVersion)
        {
            throw new ArgumentException("Invalid log segment range.");
        }

        var length = toVersion - fromVersion;
        if (length == 0)
        {
            return Task.FromResult<IReadOnlyList<TLogEntry>>(Array.Empty<TLogEntry>());
        }

        var result = new TLogEntry[length];
        for (var index = 0; index < length; index++)
        {
            result[index] = _eventLog[fromVersion + index];
        }

        return Task.FromResult<IReadOnlyList<TLogEntry>>(result);
    }

    /// <inheritdoc/>
    protected override TLogView LastConfirmedView() => _confirmedView;

    /// <inheritdoc/>
    protected override int GetConfirmedVersion() => _confirmedVersion;

    /// <inheritdoc/>
    protected override void InitializeConfirmedView(TLogView initialState)
    {
        _confirmedView = initialState;
        _confirmedVersion = 0;
    }

    /// <inheritdoc/>
    protected override async Task ClearPrimaryLogAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfTerminated();

        await EnsureInitializedAsync();
        _eventLog.Clear();
        var writeBit = FlipWriteVectorBit();
        await PersistStateAsync(writeBit, cancellationToken, "clear");
    }

    /// <inheritdoc/>
    protected override SubmissionEntry<TLogEntry> MakeSubmissionEntry(TLogEntry entry)
    {
        return new SubmissionEntry<TLogEntry> { Entry = entry };
    }

    /// <inheritdoc/>
    protected override async Task ReadAsync()
    {
        ThrowIfTerminated();
        EnterOperation("ReadAsync");

        while (true)
        {
            try
            {
                if (_stateManager.HasPendingWrites)
                {
                    var writeBit = FlipWriteVectorBit();
                    await PersistStateAsync(writeBit, CancellationToken.None, "refresh flush");
                }

                await _stateManager.RevertPendingChangesAsync(CancellationToken.None);
                UpdateConfirmedViewFromJournal(rebuild: true);

                Services.Log(LogLevel.Debug, "read success v{0}", _confirmedVersion);

                LastPrimaryIssue.Resolve(Host, Services);
                break;
            }
            catch (Exception exception)
            {
                ThrowIfTerminated();
                LastPrimaryIssue.Record(new ReadFromJournaledStateFailed { Exception = exception }, Host, Services);
            }

            Services.Log(LogLevel.Debug, "read failed {0}", LastPrimaryIssue);

            await LastPrimaryIssue.DelayBeforeRetry();
        }

        ExitOperation("ReadAsync");
    }

    /// <inheritdoc/>
    protected override async Task<int> WriteAsync()
    {
        ThrowIfTerminated();
        EnterOperation("WriteAsync");

        var updates = GetCurrentBatchOfUpdates();
        if (updates.Length == 0)
        {
            ExitOperation("WriteAsync");
            return 0;
        }

        bool writeBit;
        try
        {
            writeBit = FlipWriteVectorBit();
            foreach (var update in updates)
            {
                _eventLog.Add(update.Entry);
            }
        }
        catch (Exception exception)
        {
            await TerminallyFailAfterStagingErrorAsync(exception);
            throw;
        }

        await PersistStateAsync(writeBit, CancellationToken.None, "write");

        Services.Log(LogLevel.Debug, "write ({0} updates) success v{1}", updates.Length, _eventLog.Count);

        UpdateConfirmedViewFromJournal();
        LastPrimaryIssue.Resolve(Host, Services);

        ExitOperation("WriteAsync");
        return updates.Length;
    }

    private async Task EnsureInitializedAsync()
    {
        if (_initializationTask is null || _initializationTask.IsCanceled || _initializationTask.IsFaulted)
        {
            _initializationTask = _stateManager.InitializeAsync(CancellationToken.None).AsTask();
        }

        await _initializationTask;
    }

    private async Task PersistStateAsync(bool writeBit, CancellationToken cancellationToken, string operation)
    {
        while (true)
        {
            try
            {
                await _stateManager.WriteStateAsync(cancellationToken);
                LastPrimaryIssue.Resolve(Host, Services);
                return;
            }
            catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
            {
                _terminalFailure = exception;
                _grain.DeactivateOnIdle();
                throw;
            }
            catch (Exception exception)
            {
                LastPrimaryIssue.Record(new UpdateJournaledStateFailed { Exception = exception }, Host, Services);
                Services.Log(LogLevel.Debug, "{0} failed {1}", operation, LastPrimaryIssue);

                if (cancellationToken.IsCancellationRequested)
                {
                    _terminalFailure = new OperationCanceledException(cancellationToken);
                    _grain.DeactivateOnIdle();
                    cancellationToken.ThrowIfCancellationRequested();
                }

                await LastPrimaryIssue.DelayBeforeRetry();
                if (cancellationToken.IsCancellationRequested)
                {
                    _terminalFailure = new OperationCanceledException(cancellationToken);
                    _grain.DeactivateOnIdle();
                    cancellationToken.ThrowIfCancellationRequested();
                }

                await RecoverAfterWriteFailureAsync();
                if (writeBit == GetWriteVectorBit())
                {
                    Services.Log(LogLevel.Debug, "last {0} was actually a success v{1}", operation, _eventLog.Count);
                    LastPrimaryIssue.Resolve(Host, Services);
                    return;
                }

                _terminalFailure = exception;
                _grain.DeactivateOnIdle();
                throw;
            }
        }
    }

    private async Task RecoverAfterWriteFailureAsync()
    {
        while (true)
        {
            try
            {
                await _stateManager.RevertPendingChangesAsync(CancellationToken.None);
                UpdateConfirmedViewFromJournal(rebuild: true);
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

    private async Task TerminallyFailAfterStagingErrorAsync(Exception exception)
    {
        try
        {
            await RecoverAfterWriteFailureAsync();
        }
        finally
        {
            _terminalFailure = exception;
            _grain.DeactivateOnIdle();
        }
    }

    private void ThrowIfTerminated()
    {
        if (_terminalFailure is { } exception)
        {
            ExceptionDispatchInfo.Capture(exception).Throw();
        }
    }

    private void UpdateConfirmedViewFromJournal(bool rebuild = false)
    {
        if (rebuild || _eventLog.Count < _confirmedVersion)
        {
            InitializeConfirmedView(InitialState);
        }

        for (var index = _confirmedVersion; index < _eventLog.Count; index++)
        {
            try
            {
                Host.UpdateView(_confirmedView, _eventLog[index]);
            }
            catch (Exception exception)
            {
                Services.CaughtUserCodeException("UpdateView", nameof(UpdateConfirmedViewFromJournal), exception);
            }
        }

        _confirmedVersion = _eventLog.Count;
    }

    private bool FlipWriteVectorBit()
    {
        var value = _writeVector.Value ?? string.Empty;
        var result = StringEncodedWriteVector.FlipBit(ref value, Services.MyClusterId);
        _writeVector.Value = value;
        return result;
    }

    private bool GetWriteVectorBit() => StringEncodedWriteVector.GetBit(_writeVector.Value ?? string.Empty, Services.MyClusterId);

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
    private bool _operationInProgress;
#endif

    [System.Diagnostics.Conditional("DEBUG")]
    private void EnterOperation(string name)
    {
#if DEBUG
        Services.Log(LogLevel.Trace, "/-- enter {0}", name);
        System.Diagnostics.Debug.Assert(!_operationInProgress);
        _operationInProgress = true;
#endif
    }

    [System.Diagnostics.Conditional("DEBUG")]
    private void ExitOperation(string name)
    {
#if DEBUG
        Services.Log(LogLevel.Trace, "\\-- exit {0}", name);
        System.Diagnostics.Debug.Assert(_operationInProgress);
        _operationInProgress = false;
#endif
    }
}

#pragma warning restore ORLEANSEXP005
