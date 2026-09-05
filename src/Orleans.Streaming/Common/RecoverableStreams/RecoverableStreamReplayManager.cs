using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Configuration;
using Orleans.Streams;

namespace Orleans.Providers.Streams.Common;

internal sealed class RecoverableStreamReplayManager<TQueueMessage>
{
    private readonly object _gate;
    private readonly IRecoverableStreamQueueCache<TQueueMessage> _liveCache;
    private readonly IRecoverableStreamDataAdapter<TQueueMessage> _dataAdapter;
    private readonly IRecoverableStreamReplaySourceFactory<TQueueMessage> _sourceFactory;
    private readonly Func<IRecoverableStreamQueueCache<TQueueMessage>> _cacheFactory;
    private readonly RecoverableStreamReplayOptions _options;
    private readonly CancellationToken _shutdownToken;
    private readonly List<ReplayFragment> _fragments = [];
    private readonly LinkedList<ReplayCursor> _pending = [];
    private readonly HashSet<Task> _backgroundTasks = [];
    private readonly List<Exception> _backgroundFailures = [];
    private int _activeReaders;
    private int _disposingReaders;
    private int _replacementAdmissions;
    private bool _shutdown;

    public RecoverableStreamReplayManager(
        object gate,
        IRecoverableStreamQueueCache<TQueueMessage> liveCache,
        IRecoverableStreamDataAdapter<TQueueMessage> dataAdapter,
        IRecoverableStreamReplaySourceFactory<TQueueMessage> sourceFactory,
        Func<IRecoverableStreamQueueCache<TQueueMessage>> cacheFactory,
        RecoverableStreamReplayOptions options,
        CancellationToken shutdownToken)
    {
        _gate = gate;
        _liveCache = liveCache;
        _dataAdapter = dataAdapter;
        _sourceFactory = sourceFactory;
        _cacheFactory = cacheFactory;
        _options = options;
        _shutdownToken = shutdownToken;

        if (options.MaxConcurrentReaders <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.MaxConcurrentReaders,
                $"{nameof(RecoverableStreamReplayOptions.MaxConcurrentReaders)} must be greater than zero.");
        }

        if (options.MaxPendingReaders < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.MaxPendingReaders,
                $"{nameof(RecoverableStreamReplayOptions.MaxPendingReaders)} must be zero or greater.");
        }

        if (options.CacheSize <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.CacheSize,
                $"{nameof(RecoverableStreamReplayOptions.CacheSize)} must be greater than zero.");
        }

        if (options.ReadBatchSize <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.ReadBatchSize,
                $"{nameof(RecoverableStreamReplayOptions.ReadBatchSize)} must be greater than zero.");
        }

        if (options.TemporaryTailRetryDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.TemporaryTailRetryDelay,
                $"{nameof(RecoverableStreamReplayOptions.TemporaryTailRetryDelay)} must be zero or greater.");
        }
    }

    public bool HasPinnedLiveBoundary
    {
        get
        {
            lock (_gate)
            {
                return _fragments.Any(static fragment => fragment.LiveBoundary is not null);
            }
        }
    }

    public IQueueCacheCursor GetCursor(StreamId streamId, StreamSequenceToken? token)
    {
        lock (_gate)
        {
            try
            {
                var liveRecordToken = token is null ? null : _dataAdapter.GetRecordToken(token);
                if (token is null || _liveCache.TryGetOldestPosition(out _, out _))
                {
                    return _liveCache.GetCacheCursor(streamId, liveRecordToken);
                }
            }
            catch (QueueCacheMissException) when (token is not null)
            {
            }
            catch (ArgumentException exception) when (token is not null)
            {
                throw new DataNotAvailableException(
                    $"The requested stream token '{token}' is not valid for this recoverable stream partition.",
                    exception);
            }

            if (token is null)
            {
                return _liveCache.GetCacheCursor(streamId, null);
            }

            if (_shutdown)
            {
                throw new ObjectDisposedException(nameof(RecoverableStreamReceiver<TQueueMessage>));
            }

            StreamSequenceToken recordToken;
            try
            {
                recordToken = _dataAdapter.GetRecordToken(token);
            }
            catch (ArgumentException exception)
            {
                throw new DataNotAvailableException(
                    $"The requested stream token '{token}' is not valid for this recoverable stream partition.",
                    exception);
            }

            var cursor = new ReplayCursor(this, streamId, recordToken);
            if (TryAttachLocked(cursor))
            {
                return cursor;
            }

            if (CanWaitForFragmentLocked(cursor))
            {
                EnqueuePendingLocked(cursor);
                return cursor;
            }

            if (_activeReaders < _options.MaxConcurrentReaders)
            {
                StartReaderLocked(cursor);
                return cursor;
            }

            EnqueuePendingLocked(cursor);
            return cursor;
        }
    }

    public void OnLiveMessagesAdded(IReadOnlyList<StreamPosition> positions)
    {
        if (positions.Count == 0)
        {
            return;
        }

        lock (_gate)
        {
            foreach (var fragment in _fragments)
            {
                if (fragment.LiveBoundary is null)
                {
                    fragment.LiveBoundary = positions[0].SequenceToken;
                    fragment.LiveBoundaryEstablishedAfterTail = fragment.AtProviderTail;
                }
            }
        }
    }

    public async Task Shutdown()
    {
        Task[] tasks;
        lock (_gate)
        {
            if (_shutdown)
            {
                tasks = [.. _backgroundTasks];
            }
            else
            {
                _shutdown = true;
                foreach (var cursor in _pending)
                {
                    cursor.PendingNode = null;
                    cursor.CancelAdmission();
                }

                _pending.Clear();
                foreach (var fragment in _fragments.ToArray())
                {
                    fragment.ReceiverShutdown = true;
                    BeginFragmentDisposalLocked(fragment);
                }

                tasks = [.. _backgroundTasks];
            }
        }

        if (tasks.Length > 0)
        {
            await Task.WhenAll(tasks).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        Exception[] failures;
        lock (_gate)
        {
            failures = [.. _backgroundFailures];
        }

        if (failures is [var singleFailure])
        {
            throw singleFailure;
        }

        if (failures.Length > 1)
        {
            throw new AggregateException(failures);
        }
    }

    private bool TryAttachLocked(ReplayCursor cursor)
    {
        foreach (var fragment in _fragments)
        {
            if (fragment.Failure is not null
                || IsReclaimed(fragment, cursor.StartToken)
                || cursor.StartToken.CompareTo(fragment.StartToken) < 0
                || fragment.LiveBoundary is { } boundary && cursor.StartToken.CompareTo(boundary) >= 0)
            {
                continue;
            }

            if (!fragment.Cache.TryGetNewestPosition(out var newest, out _)
                || newest.CompareTo(cursor.StartToken) < 0)
            {
                continue;
            }

            try
            {
                var inner = fragment.Cache.GetCacheCursor(cursor.StreamId, cursor.StartToken);
                fragment.Cache.RegisterReplayStream(cursor.StreamId);
                fragment.Cursors.Add(cursor);
                cursor.Attach(fragment, inner);
                return true;
            }
            catch (QueueCacheMissException)
            {
            }
        }

        return false;
    }

    private bool CanWaitForFragmentLocked(ReplayCursor cursor)
        => _fragments.Any(fragment =>
            fragment.Failure is null
            && !IsReclaimed(fragment, cursor.StartToken)
            && cursor.StartToken.CompareTo(fragment.StartToken) >= 0
            && (fragment.LiveBoundary is null
                || cursor.StartToken.CompareTo(fragment.LiveBoundary) < 0));

    private void StartReaderLocked(ReplayCursor cursor)
    {
        _activeReaders++;
        cursor.MarkInitializing();
        TrackBackgroundTaskLocked(InitializeFragment(cursor));
    }

    private async Task InitializeFragment(ReplayCursor cursor)
    {
        IRecoverableStreamReplaySource<TQueueMessage>? source = null;
        IRecoverableStreamQueueCache<TQueueMessage>? cache = null;
        try
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cursor.CancellationToken,
                _shutdownToken);
            source = await _sourceFactory.Create(cursor.StreamId, cursor.StartToken, cancellation.Token);
            cache = _cacheFactory();
            lock (_gate)
            {
                if (_shutdown || cursor.IsDisposed)
                {
                    throw new OperationCanceledException(cursor.CancellationToken);
                }

                _liveCache.TryGetOldestPosition(out var liveBoundary, out _);
                var replayCursor = cache.GetCacheCursor(cursor.StreamId, cursor.StartToken);
                if (replayCursor is not IQueueCacheCursorProgress)
                {
                    replayCursor.Dispose();
                    throw new InvalidOperationException(
                        $"Replay cache cursors must implement {nameof(IQueueCacheCursorProgress)}.");
                }

                var fragment = new ReplayFragment(
                    cursor.StartToken,
                    source,
                    cache,
                    liveBoundary,
                    CancellationTokenSource.CreateLinkedTokenSource(_shutdownToken));
                cache.RegisterReplayStream(cursor.StreamId);
                fragment.Cursors.Add(cursor);
                cursor.Attach(fragment, replayCursor);
                _fragments.Add(fragment);
                source = null;
                cache = null;
                PromotePendingLocked();
            }
        }
        catch (Exception exception)
        {
            Exception failure = exception;
            if (cache is not null)
            {
                try
                {
                    cache.Dispose();
                }
                catch (Exception cleanupException)
                {
                    failure = new AggregateException(failure, cleanupException);
                }
            }

            if (source is not null)
            {
                bool receiverShutdown;
                lock (_gate)
                {
                    receiverShutdown = _shutdown;
                }

                try
                {
                    if (receiverShutdown)
                    {
                        await source.ShutdownAsync(CancellationToken.None);
                    }
                    else
                    {
                        await source.DisposeAsync();
                    }
                }
                catch (Exception cleanupException)
                {
                    failure = new AggregateException(failure, cleanupException);
                }
            }

            lock (_gate)
            {
                _activeReaders--;
                cursor.FailAdmission(failure);
                PromotePendingLocked();
            }
        }
    }

    private void PromotePendingLocked()
    {
        for (var node = _pending.First; node is not null;)
        {
            var next = node.Next;
            var cursor = node.Value;
            if (cursor.IsDisposed)
            {
                _pending.Remove(node);
                cursor.PendingNode = null;
                ReleaseReplacementAdmissionLocked(cursor);
                node = next;
                continue;
            }

            if (TryAttachLocked(cursor))
            {
                _pending.Remove(node);
                cursor.PendingNode = null;
                ReleaseReplacementAdmissionLocked(cursor);
            }

            node = next;
        }

        while (_activeReaders < _options.MaxConcurrentReaders)
        {
            LinkedListNode<ReplayCursor>? selected = null;
            for (var node = _pending.First; node is not null; node = node.Next)
            {
                if (!CanWaitForFragmentLocked(node.Value))
                {
                    selected = node;
                    break;
                }
            }

            if (selected is null)
            {
                return;
            }

            var cursor = selected.Value;
            _pending.Remove(selected);
            cursor.PendingNode = null;
            ReleaseReplacementAdmissionLocked(cursor);
            StartReaderLocked(cursor);
        }
    }

    private void EnqueuePendingLocked(ReplayCursor cursor)
    {
        if (_pending.Count >= _options.MaxPendingReaders)
        {
            if (_replacementAdmissions >= _disposingReaders)
            {
                throw new InvalidOperationException(
                    $"The retained-history replay admission queue reached its configured limit of {_options.MaxPendingReaders} cursors.");
            }

            cursor.IsReplacementAdmission = true;
            _replacementAdmissions++;
        }

        cursor.PendingNode = _pending.AddLast(cursor);
    }

    private void ReleaseReplacementAdmissionLocked(ReplayCursor cursor)
    {
        if (cursor.IsReplacementAdmission)
        {
            cursor.IsReplacementAdmission = false;
            _replacementAdmissions--;
        }
    }

    private async ValueTask<QueueCacheCursorMoveNextResult> MoveNextAsync(
        ReplayCursor cursor,
        CancellationToken cancellationToken)
    {
        await cursor.WaitForAdmission(cancellationToken);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            cursor.CancellationToken.ThrowIfCancellationRequested();

            ReplayFragment? fragment;
            lock (_gate)
            {
                if (cursor.IsDisposed)
                {
                    return QueueCacheCursorMoveNextResult.Completed;
                }

                if (cursor.LiveCursor is { } liveCursor)
                {
                    cursor.HasPendingLiveHandoff = false;
                    return liveCursor.MoveNext()
                        ? QueueCacheCursorMoveNextResult.ItemAvailable
                        : QueueCacheCursorMoveNextResult.Completed;
                }

                if (TryAdvanceReplayLocked(cursor, out var result))
                {
                    return result;
                }

                fragment = cursor.Fragment;
            }

            if (fragment is null)
            {
                return QueueCacheCursorMoveNextResult.Completed;
            }

            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                cursor.CancellationToken,
                fragment.Cancellation.Token);
            await fragment.ReadLock.WaitAsync(linkedCancellation.Token);
            RecoverableStreamReplayReadResult<TQueueMessage> read = default;
            var readCompleted = false;
            try
            {
                lock (_gate)
                {
                    if (cursor.IsDisposed || cursor.LiveCursor is not null)
                    {
                        continue;
                    }

                    if (TryAdvanceReplayLocked(cursor, out var result))
                    {
                        return result;
                    }
                }

                var maxCount = Math.Min(_options.ReadBatchSize, fragment.Cache.GetMaxAddCount());
                if (maxCount <= 0)
                {
                    if (_options.TemporaryTailRetryDelay > TimeSpan.Zero)
                    {
                        await Task.Delay(_options.TemporaryTailRetryDelay, linkedCancellation.Token);
                    }

                    return QueueCacheCursorMoveNextResult.TemporaryTail;
                }

                read = await fragment.Source.Read(maxCount, linkedCancellation.Token);
                readCompleted = true;
                if (read.Messages.Count > 0)
                {
                    IReadOnlyList<TQueueMessage> admitted = read.Messages;
                    lock (_gate)
                    {
                        if (fragment.LiveBoundary is { } boundary)
                        {
                            var beforeBoundary = new List<TQueueMessage>(read.Messages.Count);
                            foreach (var message in read.Messages)
                            {
                                if (_dataAdapter.GetStreamPosition(message).SequenceToken.CompareTo(boundary) >= 0)
                                {
                                    fragment.ReachedLiveBoundary = true;
                                    break;
                                }

                                beforeBoundary.Add(message);
                            }

                            admitted = beforeBoundary;
                        }

                        if (admitted.Count > 0)
                        {
                            _ = fragment.Cache.Add(admitted, DateTime.UtcNow);
                            PromotePendingLocked();
                        }

                        fragment.AtProviderTail |= read.IsAtTail;
                    }

                    fragment.Source.MessagesAdded(read.Messages);
                }
                else
                {
                    lock (_gate)
                    {
                        fragment.AtProviderTail |= read.IsAtTail;
                    }
                }
            }
            catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
            {
                if (readCompleted && read.Messages.Count > 0)
                {
                    fragment.Source.MessagesAddFailed(read.Messages);
                }

                throw;
            }
            catch (Exception exception)
            {
                Exception failure = exception;
                if (readCompleted && read.Messages.Count > 0)
                {
                    try
                    {
                        fragment.Source.MessagesAddFailed(read.Messages);
                    }
                    catch (Exception callbackException)
                    {
                        failure = new AggregateException(failure, callbackException);
                    }
                }

                lock (_gate)
                {
                    fragment.Failure = ExceptionDispatchInfo.Capture(failure);
                    _fragments.Remove(fragment);
                }

                ExceptionDispatchInfo.Capture(failure).Throw();
                throw;
            }
            finally
            {
                fragment.ReadLock.Release();
            }

            if (read.Messages.Count == 0 && !read.IsAtTail)
            {
                if (_options.TemporaryTailRetryDelay > TimeSpan.Zero)
                {
                    await Task.Delay(_options.TemporaryTailRetryDelay, linkedCancellation.Token);
                }

                return QueueCacheCursorMoveNextResult.TemporaryTail;
            }
        }
    }

    private bool TryAdvanceReplayLocked(
        ReplayCursor cursor,
        out QueueCacheCursorMoveNextResult result)
    {
        if (cursor.LiveCursor is { } liveCursor)
        {
            cursor.HasPendingLiveHandoff = false;
            result = liveCursor.MoveNext()
                ? QueueCacheCursorMoveNextResult.ItemAvailable
                : QueueCacheCursorMoveNextResult.Completed;
            return true;
        }

        var fragment = cursor.Fragment;
        var replayCursor = cursor.HistoricalCursor;
        if (fragment is null || replayCursor is null)
        {
            result = QueueCacheCursorMoveNextResult.Completed;
            return true;
        }

        fragment.Failure?.Throw();

        if (replayCursor.MoveNext())
        {
            var current = replayCursor.GetCurrent(out var exception);
            if (exception is not null)
            {
                throw exception;
            }

            if (current is null)
            {
                throw new InvalidOperationException("A replay cursor advanced without providing a current record.");
            }

            if (fragment.LiveBoundary is { } boundary
                && current.SequenceToken.CompareTo(boundary) >= 0)
            {
                HandoffLocked(cursor, fragment);
                result = QueueCacheCursorMoveNextResult.Completed;
                return true;
            }

            result = QueueCacheCursorMoveNextResult.ItemAvailable;
            return true;
        }

        ReclaimFragmentLocked(fragment);
        var safeToken = (replayCursor as IQueueCacheCursorProgress)?.SafeSequenceToken;
        if (fragment.LiveBoundary is { } liveBoundary
            && (fragment.LiveBoundaryEstablishedAfterTail
                || fragment.ReachedLiveBoundary
                || safeToken is not null && safeToken.CompareTo(liveBoundary) >= 0))
        {
            HandoffLocked(cursor, fragment);
            result = QueueCacheCursorMoveNextResult.Completed;
            return true;
        }

        if (fragment.LiveBoundary is null && fragment.AtProviderTail)
        {
            HandoffLocked(cursor, fragment);
            result = QueueCacheCursorMoveNextResult.Completed;
            return true;
        }

        if (fragment.LiveBoundary is not null
            && fragment.AtProviderTail
            && !fragment.ReachedLiveBoundary)
        {
            throw new DataNotAvailableException(
                $"Retained stream history ended before the pinned live-cache handoff boundary {fragment.LiveBoundary}.");
        }

        result = default;
        return false;
    }

    private void HandoffLocked(ReplayCursor cursor, ReplayFragment fragment)
    {
        var liveCursor = _liveCache.GetCacheCursor(
            cursor.StreamId,
            fragment.LiveBoundary ?? cursor.StartToken);
        if (cursor.DeliveredThrough is { } deliveredThrough
            && liveCursor is IQueueCacheCursorProgress progressCursor)
        {
            progressCursor.SetDeliveredThrough(deliveredThrough);
        }

        cursor.HistoricalCursor?.Dispose();
        cursor.HistoricalCursor = null;
        cursor.Fragment = null;
        cursor.LiveCursor = liveCursor;
        cursor.HasPendingLiveHandoff = true;
        fragment.Cursors.Remove(cursor);
        UnregisterReplayStreamIfUnused(fragment, cursor.StreamId);
        if (fragment.Cursors.Count == 0)
        {
            BeginFragmentDisposalLocked(fragment);
        }
    }

    private void ReclaimFragmentLocked(ReplayFragment fragment)
    {
        StreamSequenceToken? earliest = null;
        var inclusive = true;
        foreach (var cursor in fragment.Cursors)
        {
            StreamSequenceToken current;
            var currentInclusive = true;
            if (cursor.HistoricalCursor is IQueueCacheCursorProgress progress
                && progress.SafeSequenceToken is { } safe)
            {
                current = safe;
            }
            else
            {
                current = cursor.StartToken;
                currentInclusive = false;
            }

            var comparison = earliest is null ? -1 : current.CompareTo(earliest);
            if (earliest is null || comparison < 0)
            {
                earliest = current;
                inclusive = currentInclusive;
            }
            else if (comparison == 0)
            {
                inclusive &= currentInclusive;
            }
        }

        if (earliest is not null)
        {
            fragment.Cache.UpdateReplayProgress(earliest, inclusive, DateTime.UtcNow);
            fragment.ReclaimedBoundary = earliest;
            fragment.ReclaimedBoundaryInclusive = inclusive;
            if (inclusive)
            {
                fragment.Source.UpdateProgress(earliest);
            }
        }
    }

    private void DisposeCursor(ReplayCursor cursor)
    {
        lock (_gate)
        {
            if (!cursor.MarkDisposed())
            {
                return;
            }

            if (cursor.PendingNode is { } pendingNode)
            {
                _pending.Remove(pendingNode);
                cursor.PendingNode = null;
                ReleaseReplacementAdmissionLocked(cursor);
            }

            cursor.HistoricalCursor?.Dispose();
            cursor.HistoricalCursor = null;
            cursor.LiveCursor?.Dispose();
            cursor.LiveCursor = null;
            if (cursor.Fragment is { } fragment)
            {
                fragment.Cursors.Remove(cursor);
                UnregisterReplayStreamIfUnused(fragment, cursor.StreamId);
                cursor.Fragment = null;
                if (fragment.Cursors.Count == 0)
                {
                    BeginFragmentDisposalLocked(fragment);
                }
            }

            cursor.CancelAdmission();
            PromotePendingLocked();
        }
    }

    private void BeginFragmentDisposalLocked(ReplayFragment fragment)
    {
        if (fragment.DisposalStarted)
        {
            return;
        }

        fragment.DisposalStarted = true;
        _fragments.Remove(fragment);
        fragment.Cancellation.Cancel();
        _disposingReaders++;
        TrackBackgroundTaskLocked(DisposeFragment(fragment));
    }

    private static bool IsReclaimed(
        ReplayFragment fragment,
        StreamSequenceToken token)
    {
        if (fragment.ReclaimedBoundary is not { } boundary)
        {
            return false;
        }

        var comparison = token.CompareTo(boundary);
        return comparison < 0 || comparison == 0 && fragment.ReclaimedBoundaryInclusive;
    }

    private static void UnregisterReplayStreamIfUnused(
        ReplayFragment fragment,
        StreamId streamId)
    {
        if (!fragment.Cursors.Any(cursor => cursor.StreamId.Equals(streamId)))
        {
            fragment.Cache.UnregisterReplayStream(streamId);
        }
    }

    private async Task DisposeFragment(ReplayFragment fragment)
    {
        Exception? exception = null;
        try
        {
            await fragment.ReadLock.WaitAsync();
            try
            {
                if (fragment.ReceiverShutdown)
                {
                    await fragment.Source.ShutdownAsync(CancellationToken.None);
                }
                else
                {
                    await fragment.Source.DisposeAsync();
                }
            }
            finally
            {
                fragment.ReadLock.Release();
            }
        }
        catch (Exception current)
        {
            exception = current;
        }

        try
        {
            fragment.Cache.Dispose();
        }
        catch (Exception current)
        {
            exception = exception is null ? current : new AggregateException(exception, current);
        }

        fragment.Cancellation.Dispose();
        fragment.ReadLock.Dispose();
        lock (_gate)
        {
            _activeReaders--;
            _disposingReaders--;
            PromotePendingLocked();
        }

        if (exception is not null)
        {
            throw exception;
        }
    }

    private void TrackBackgroundTaskLocked(Task task)
    {
        _backgroundTasks.Add(task);
        _ = task.ContinueWith(
            static (completed, state) =>
            {
                var manager = (RecoverableStreamReplayManager<TQueueMessage>)state!;
                lock (manager._gate)
                {
                    if (completed.Exception is { } aggregate)
                    {
                        manager._backgroundFailures.AddRange(aggregate.Flatten().InnerExceptions);
                    }

                    manager._backgroundTasks.Remove(completed);
                }
            },
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private sealed class ReplayFragment(
        StreamSequenceToken startToken,
        IRecoverableStreamReplaySource<TQueueMessage> source,
        IRecoverableStreamQueueCache<TQueueMessage> cache,
        StreamSequenceToken? liveBoundary,
        CancellationTokenSource cancellation)
    {
        public StreamSequenceToken StartToken { get; } = startToken;
        public IRecoverableStreamReplaySource<TQueueMessage> Source { get; } = source;
        public IRecoverableStreamQueueCache<TQueueMessage> Cache { get; } = cache;
        public StreamSequenceToken? LiveBoundary { get; set; } = liveBoundary;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public SemaphoreSlim ReadLock { get; } = new(1, 1);
        public HashSet<ReplayCursor> Cursors { get; } = [];
        public bool ReachedLiveBoundary { get; set; }
        public bool AtProviderTail { get; set; }
        public bool LiveBoundaryEstablishedAfterTail { get; set; }
        public bool ReceiverShutdown { get; set; }
        public bool DisposalStarted { get; set; }
        public ExceptionDispatchInfo? Failure { get; set; }
        public StreamSequenceToken? ReclaimedBoundary { get; set; }
        public bool ReclaimedBoundaryInclusive { get; set; }
    }

    private sealed class ReplayCursor : IAsyncQueueCacheCursor, IQueueCacheCursorProgress, IQueueCacheCursorReplayState
    {
        private readonly RecoverableStreamReplayManager<TQueueMessage> _owner;
        private readonly TaskCompletionSource _admission =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenSource _cancellation = new();
        private readonly CancellationToken _cancellationToken;
        private int _moveNextActive;
        private int _disposed;

        public ReplayCursor(
            RecoverableStreamReplayManager<TQueueMessage> owner,
            StreamId streamId,
            StreamSequenceToken startToken)
        {
            _owner = owner;
            StreamId = streamId;
            StartToken = startToken;
            _cancellationToken = _cancellation.Token;
        }

        public StreamId StreamId { get; }
        public StreamSequenceToken StartToken { get; }
        public CancellationToken CancellationToken => _cancellationToken;
        public ReplayFragment? Fragment { get; set; }
        public IQueueCacheCursor? HistoricalCursor { get; set; }
        public IQueueCacheCursor? LiveCursor { get; set; }
        public StreamSequenceToken? DeliveredThrough { get; private set; }
        public LinkedListNode<ReplayCursor>? PendingNode { get; set; }
        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;
        public bool IsReplaying => LiveCursor is null && !IsDisposed;
        public bool HasPendingLiveHandoff { get; set; }
        public bool IsReplacementAdmission { get; set; }

        public StreamSequenceToken? SafeSequenceToken
            => (LiveCursor ?? HistoricalCursor) is IQueueCacheCursorProgress progress
                ? progress.SafeSequenceToken
                : null;

        public void Attach(ReplayFragment fragment, IQueueCacheCursor cursor)
        {
            Fragment = fragment;
            HistoricalCursor = cursor;
            if (DeliveredThrough is { } deliveredThrough
                && cursor is IQueueCacheCursorProgress progressCursor)
            {
                progressCursor.SetDeliveredThrough(deliveredThrough);
            }

            _admission.TrySetResult();
        }

        public void MarkInitializing()
        {
        }

        public void FailAdmission(Exception exception) => _admission.TrySetException(exception);

        public void CancelAdmission() => _admission.TrySetCanceled(_cancellationToken);

        public async ValueTask WaitForAdmission(CancellationToken cancellationToken)
            => await _admission.Task.WaitAsync(cancellationToken);

        public bool MoveNext()
        {
            if (!_admission.Task.IsCompletedSuccessfully)
            {
                return false;
            }

            lock (_owner._gate)
            {
                return _owner.TryAdvanceReplayLocked(this, out var result)
                    && result == QueueCacheCursorMoveNextResult.ItemAvailable;
            }
        }

        public async ValueTask<QueueCacheCursorMoveNextResult> MoveNextAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _moveNextActive, 1) != 0)
            {
                throw new InvalidOperationException("Calls to a historical queue cache cursor are non-reentrant.");
            }

            try
            {
                return await _owner.MoveNextAsync(this, cancellationToken);
            }
            finally
            {
                Volatile.Write(ref _moveNextActive, 0);
            }
        }

        public IBatchContainer? GetCurrent(out Exception? exception)
        {
            var cursor = LiveCursor ?? HistoricalCursor;
            if (cursor is null)
            {
                exception = null;
                return null;
            }

            return cursor.GetCurrent(out exception);
        }

        public void Refresh(StreamSequenceToken token) => LiveCursor?.Refresh(token);

        public void RecordDeliveryFailure()
        {
            lock (_owner._gate)
            {
                (LiveCursor ?? HistoricalCursor)?.RecordDeliveryFailure();
            }
        }

        public void RecordDeliverySuccess()
        {
            lock (_owner._gate)
            {
                if ((LiveCursor ?? HistoricalCursor) is IQueueCacheCursorProgress progress)
                {
                    progress.RecordDeliverySuccess();
                }

                if (Fragment is { } fragment)
                {
                    _owner.ReclaimFragmentLocked(fragment);
                }
            }
        }

        public void SetDeliveredThrough(StreamSequenceToken token)
        {
            lock (_owner._gate)
            {
                DeliveredThrough = token;
                if ((LiveCursor ?? HistoricalCursor) is IQueueCacheCursorProgress progress)
                {
                    progress.SetDeliveredThrough(token);
                }
            }
        }

        public void Dispose() => _owner.DisposeCursor(this);

        public bool MarkDisposed()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return false;
            }

            _cancellation.Cancel();
            _cancellation.Dispose();
            return true;
        }
    }
}
