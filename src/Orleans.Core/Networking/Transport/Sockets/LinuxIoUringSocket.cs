#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;
using Orleans.Serialization.Buffers;

namespace Orleans.Connections.Transport.Sockets;

internal sealed unsafe class LinuxIoUringSocketReceiver : LinuxIoUringOperation, ISocketReceiver
{
    private const int MaximumScatterBuffers = 8;
    private readonly IntPtr _iovecs;
    private readonly IntPtr _messageHeader;

    public LinuxIoUringSocketReceiver()
    {
        try
        {
            _iovecs = (IntPtr)NativeMemory.Alloc((nuint)(MaximumScatterBuffers * sizeof(IoVector)));
            _messageHeader = (IntPtr)NativeMemory.Alloc((nuint)sizeof(MessageHeader));
        }
        catch
        {
            NativeMemory.Free((void*)_iovecs);
            base.Dispose();
            throw;
        }
    }

    public ValueTask ReceiveAsync(Socket socket, List<ArraySegment<byte>> buffers)
    {
        if (buffers.Count == 0)
        {
            throw new ArgumentException("At least one receive buffer is required.", nameof(buffers));
        }

        if (buffers.Count > MaximumScatterBuffers)
        {
            throw new ArgumentOutOfRangeException(
                nameof(buffers),
                buffers.Count,
                $"A scatter receive supports at most {MaximumScatterBuffers} buffers.");
        }

        BeginPreparation();
        try
        {
            var vectors = (IoVector*)_iovecs;
            for (var i = 0; i < buffers.Count; i++)
            {
                var buffer = buffers[i];
                var array = buffer.Array ?? throw new ArgumentException("The receive buffer must be array-backed.", nameof(buffers));
                vectors[i] = new()
                {
                    Base = Marshal.UnsafeAddrOfPinnedArrayElement(array, buffer.Offset),
                    Length = (nuint)buffer.Count,
                };
            }

            *(MessageHeader*)_messageHeader = new()
            {
                Vectors = _iovecs,
                VectorCount = (nuint)buffers.Count,
            };
        }
        catch
        {
            CancelPreparation();
            throw;
        }

        return SubmitPrepared(
            socket,
            _messageHeader,
            bufferLength: 1,
            LinuxIoUringEngine.ReceiveMessageOperation,
            waitForNotification: false);
    }

    public ValueTask StopAsync() => default;

    public override void Dispose()
    {
        base.Dispose();
        NativeMemory.Free((void*)_iovecs);
        NativeMemory.Free((void*)_messageHeader);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoVector
    {
        internal IntPtr Base;
        internal nuint Length;
    }

    [StructLayout(LayoutKind.Explicit, Size = 56)]
    private struct MessageHeader
    {
        [FieldOffset(0)] private IntPtr _name;
        [FieldOffset(8)] private uint _nameLength;
        [FieldOffset(16)] internal IntPtr Vectors;
        [FieldOffset(24)] internal nuint VectorCount;
        [FieldOffset(32)] private IntPtr _control;
        [FieldOffset(40)] private nuint _controlLength;
        [FieldOffset(48)] private int _flags;
    }
}

internal sealed unsafe class LinuxIoUringSocketMultishotReceiver : LinuxIoUringOperation, IOwnedPageSocketReceiver
{
    private const int BufferCount = 16;
    private const int BufferSize = 16 * 1024;
    private const uint IncrementalBufferConsumption = 2;
    private readonly BufferState[] _buffers = new BufferState[BufferCount];
    private readonly Queue<ReceivedSegment> _receivedSegments = new();
    private readonly ReceiveWaiter _receiveWaiter = new();
    private readonly object _stateLock = new();
    private readonly ushort _bufferGroup;
    private IntPtr _bufferRing;
    private ArcBufferWriter? _receiveWriter;
    private TaskCompletionSource? _stopCompletion;
    private Socket? _socketForRearm;
    private bool _bufferGroupReleased;
    private bool _stopping;
    private bool _finished;
    private bool _needsRearm;
    private bool _receiveWaiting;
    private int _refillQueued;
    private ulong _pendingRefillMask;
    private Exception? _terminalError;
    private ArcBufferPage? _firstAdoptedPage;
    private long _adoptedPageCount;
    private long _completedSegmentCount;
    private long _finalBufferCount;
    private long _replacementPageCount;
    private long _noBufferCompletionCount;
    private long _receiveStartCount;

    public LinuxIoUringSocketMultishotReceiver(LinuxIoUringEngine? engine = null)
        : base(engine)
    {
        var bufferGroupAllocated = false;
        try
        {
            _bufferGroup = Engine.AllocateBufferGroup();
            bufferGroupAllocated = true;
            for (var i = 0; i < BufferCount; i++)
            {
                AssignFreshPage(ref _buffers[i], BufferOwnership.ReceiverOwned);
            }
        }
        catch
        {
            ReleaseReceiverPages();
            if (bufferGroupAllocated)
            {
                ReleaseBufferGroup();
            }

            base.Dispose();
            throw;
        }
    }

    public ValueTask ReceiveAsync(Socket socket, List<ArraySegment<byte>> buffers)
        => throw new NotSupportedException("The multishot receiver writes received pages directly to an ArcBufferWriter.");

    public ValueTask ReceiveAsync(Socket socket, ArcBufferWriter writer)
    {
        lock (_stateLock)
        {
            if (_receiveWaiting)
            {
                throw new InvalidOperationException("A multishot receive is already waiting for data.");
            }

            var appended = AppendReceivedPages(writer);
            if (appended > 0)
            {
                BytesTransferred = appended;
                SocketError = SocketError.Success;
                Error = null;
                return default;
            }

            if (_terminalError is { } terminalError)
            {
                return ValueTask.FromException(terminalError);
            }

            if (_finished && !_needsRearm)
            {
                BytesTransferred = 0;
                SocketError = SocketError.Success;
                Error = null;
                return default;
            }

            _receiveWriter = writer;
            var pendingReceive = _receiveWaiter.Reset();
            _receiveWaiting = true;
            try
            {
                RefillFreeBuffers();
                if (!IsPending)
                {
                    StartReceive(socket);
                }
            }
            catch
            {
                _receiveWriter = null;
                _receiveWaiting = false;
                throw;
            }

            return pendingReceive;
        }
    }

    [SuppressMessage(
        "Reliability",
        "CA2000",
        Justification = "The cancellation operation is owned by the io_uring engine and disposes itself after completion.")]
    public ValueTask StopAsync()
    {
        lock (_stateLock)
        {
            if (!IsPending)
            {
                if (_receiveWaiting && _needsRearm)
                {
                    _terminalError = new SocketException((int)SocketError.OperationAborted);
                    _finished = true;
                    _needsRearm = false;
                    CompleteWaitingReceive();
                }

                return default;
            }

            if (_stopCompletion is not null)
            {
                return new ValueTask(_stopCompletion.Task);
            }

            _stopping = true;
            _stopCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = new LinuxIoUringCancelOperation(
                Engine,
                UserData,
                CompleteCancellation,
                CompleteCancellation);
            return new ValueTask(_stopCompletion.Task);
        }
    }

    internal override bool IsMultishot => true;

    internal override void PrepareSubmission(LinuxIoUringEngine.IoUringSubmission* submission)
    {
        if (_bufferRing == IntPtr.Zero)
        {
            var error = 0;
            _bufferRing = LinuxIoUringEngine.Native.SetupBufRing(
                ref Engine.Ring,
                BufferCount,
                _bufferGroup,
                IncrementalBufferConsumption,
                &error);
            if (_bufferRing == IntPtr.Zero)
            {
                throw new InvalidOperationException($"Unable to create the io_uring provided buffer ring. errno={error}");
            }

            var ring = (LinuxIoUringEngine.IoUringBufferRing*)_bufferRing;
            ring->Tail = 0;
            var added = 0;
            for (var i = 0; i < BufferCount; i++)
            {
                var state = _buffers[i];
                var page = state.Page
                    ?? throw new InvalidOperationException("A provided-buffer page was released before initial publication.");
                if (state.Ownership != BufferOwnership.ReceiverOwned)
                {
                    throw new InvalidOperationException("A provided-buffer page has invalid initial ownership.");
                }

                AddBuffer(ring, page, i, added++);
            }

            LinuxIoUringEngine.AdvanceBufferRing(ring, added);
            for (var i = 0; i < BufferCount; i++)
            {
                _buffers[i].Ownership = BufferOwnership.Published;
            }
        }

        submission->Flags = LinuxIoUringEngine.BufferSelect;
        submission->IoPriority = checked((ushort)LinuxIoUringEngine.ReceiveMultishot);
        submission->BufferIndex = _bufferGroup;
    }

    internal override void HandleCompletion(int result, uint flags)
    {
        lock (_stateLock)
        {
            if (!IsPending)
            {
                throw new InvalidOperationException("A multishot receive completed after it had been retired.");
            }

            if ((flags & LinuxIoUringEngine.CompletionIsNotification) != 0)
            {
                Fail(new InvalidOperationException("A multishot receive produced a notification CQE."));
                return;
            }

            if (result > 0)
            {
                const uint AllowedCompletionFlags =
                    LinuxIoUringEngine.CompletionHasBuffer
                    | LinuxIoUringEngine.CompletionHasMore
                    | LinuxIoUringEngine.CompletionHasSocketData
                    | LinuxIoUringEngine.CompletionBufferMore;
                var completionFlags = flags & ushort.MaxValue;
                if ((completionFlags & LinuxIoUringEngine.CompletionHasBuffer) == 0
                    || (completionFlags & ~AllowedCompletionFlags) != 0)
                {
                    Fail(new InvalidOperationException("A multishot receive produced an invalid provided-buffer CQE."));
                    return;
                }

                var bufferId = (int)(flags >> 16);
                if ((uint)bufferId >= BufferCount)
                {
                    Fail(new InvalidOperationException("A multishot receive produced an invalid buffer ID."));
                    return;
                }

                ref var state = ref _buffers[bufferId];
                if (state.Page is not { } page
                    || state.Ownership is not (BufferOwnership.Published or BufferOwnership.PartiallyConsumed))
                {
                    Fail(new InvalidOperationException("A multishot receive produced a stale or reused buffer ID."));
                    return;
                }

                var offset = state.CompletedOffset;
                if (result > page.Array.Length - offset)
                {
                    Fail(new InvalidOperationException("A multishot receive exceeded the remaining provided buffer capacity."));
                    return;
                }

                var finalOffset = offset + result;
                var bufferHasMore = (completionFlags & LinuxIoUringEngine.CompletionBufferMore) != 0;
                var receiveHasMore = (completionFlags & LinuxIoUringEngine.CompletionHasMore) != 0;
                if (bufferHasMore && (!receiveHasMore || finalOffset == page.Array.Length))
                {
                    Fail(new InvalidOperationException("A multishot receive produced inconsistent incremental-buffer flags."));
                    return;
                }

                _receivedSegments.Enqueue(
                    new ReceivedSegment(
                        bufferId,
                        state.Generation,
                        page,
                        state.PageVersion,
                        offset,
                        result,
                        FinalForBuffer: !bufferHasMore));
                state.CompletedOffset = finalOffset;
                state.Ownership = bufferHasMore
                    ? BufferOwnership.PartiallyConsumed
                    : BufferOwnership.FinalQueued;
                _completedSegmentCount++;
                if (!bufferHasMore)
                {
                    _finalBufferCount++;
                }

                if (!receiveHasMore)
                {
                    Finish(needsRearm: true);
                }

                CompleteWaitingReceive();
                return;
            }

            if ((flags & ~LinuxIoUringEngine.CompletionHasSocketData) != 0)
            {
                Fail(new InvalidOperationException($"A terminal multishot receive produced invalid CQE flags 0x{flags:x8} for result {result}."));
                return;
            }

            if (result == 0)
            {
                Finish();
                CompleteWaitingReceive();
                return;
            }

            var socketError = LinuxIoUringEngine.MapSocketErrorCode(-result);
            if (socketError == SocketError.OperationAborted && (_stopping || Engine.IsShuttingDown))
            {
                var hasWaitingReceive = _receiveWaiting;
                Finish(
                    hasWaitingReceive ? new SocketException((int)SocketError.OperationAborted) : null,
                    needsRearm: _stopping && !Engine.IsShuttingDown && !hasWaitingReceive,
                    stopSucceeded: true);
                CompleteWaitingReceive();
                return;
            }

            if (socketError == SocketError.NoBufferSpaceAvailable)
            {
                _noBufferCompletionCount++;
                if (_stopping)
                {
                    var hasWaitingReceive = _receiveWaiting;
                    Finish(
                        hasWaitingReceive ? new SocketException((int)SocketError.OperationAborted) : null,
                        needsRearm: !Engine.IsShuttingDown && !hasWaitingReceive,
                        stopSucceeded: true);
                    CompleteWaitingReceive();
                    return;
                }

                Finish(needsRearm: true);
                if (_receiveWaiting
                    && _receivedSegments.Count == 0
                    && _socketForRearm is { } socket)
                {
                    StartReceive(socket);
                }

                CompleteWaitingReceive();
                return;
            }

            Finish(new SocketException((int)socketError));
            CompleteWaitingReceive();
        }
    }

    internal override void Complete(Exception error)
    {
        lock (_stateLock)
        {
            if (!IsPending)
            {
                return;
            }

            _terminalError = error;
            _finished = true;
            _needsRearm = false;
            RetireSubmission();
            CompleteWaitingReceive();
            _stopCompletion?.TrySetException(error);
        }
    }

    internal override void OnEngineShutdown()
    {
        lock (_stateLock)
        {
            if (_bufferRing != IntPtr.Zero)
            {
                LinuxIoUringEngine.Native.FreeBufRing(ref Engine.Ring, _bufferRing, BufferCount, _bufferGroup);
                _bufferRing = IntPtr.Zero;
            }
        }
    }

    internal override void OnEngineShutdownComplete()
    {
        lock (_stateLock)
        {
            ReleaseReceiverPages();
            ReleaseBufferGroup();
        }
    }

    public override void Dispose()
    {
        if (IsPending)
        {
            throw new InvalidOperationException("An active multishot receive cannot be disposed.");
        }

        UnregisterBufferRing();

        lock (_stateLock)
        {
            ReleaseReceiverPages();
            ReleaseBufferGroup();
        }

        base.Dispose();
    }

    internal ushort BufferGroup => _bufferGroup;

    internal long AdoptedPageCount => Volatile.Read(ref _adoptedPageCount);

    internal long CompletedSegmentCount => Volatile.Read(ref _completedSegmentCount);

    internal long FinalBufferCount => Volatile.Read(ref _finalBufferCount);

    internal long ReplacementPageCount => Volatile.Read(ref _replacementPageCount);

    internal long NoBufferCompletionCount => Volatile.Read(ref _noBufferCompletionCount);

    internal long ReceiveStartCount => Volatile.Read(ref _receiveStartCount);

    internal ArcBufferPage? FirstAdoptedPage => _firstAdoptedPage;

    internal long PayloadCopyCount => 0;

    internal int ActiveIncrementalPageCount
    {
        get
        {
            lock (_stateLock)
            {
                var result = 0;
                foreach (var state in _buffers)
                {
                    if (state.Ownership == BufferOwnership.PartiallyConsumed)
                    {
                        result++;
                    }
                }

                return result;
            }
        }
    }

    internal int DrainReceivedPages(ArcBufferWriter writer)
    {
        lock (_stateLock)
        {
            return AppendReceivedPages(writer);
        }
    }

    internal void TransitionToOneShot(ArcBufferWriter writer)
    {
        if (IsPending)
        {
            throw new InvalidOperationException("A multishot receive must stop before transitioning to one-shot receive.");
        }

        DrainReceivedPages(writer);
        UnregisterBufferRing();
        lock (_stateLock)
        {
            AppendReceivedPages(writer);
            _pendingRefillMask = 0;
            Volatile.Write(ref _refillQueued, 0);
            for (var i = 0; i < BufferCount; i++)
            {
                ref var state = ref _buffers[i];
                switch (state.Ownership)
                {
                    case BufferOwnership.Free:
                        AssignFreshPage(ref state, BufferOwnership.ReceiverOwned);
                        break;
                    case BufferOwnership.ReceiverOwned:
                    case BufferOwnership.PendingPublication:
                    case BufferOwnership.Published:
                        if (state.Adopted)
                        {
                            throw new InvalidOperationException("A reusable provided-buffer page is still adopted by the receive writer.");
                        }

                        state.CompletedOffset = 0;
                        state.Ownership = BufferOwnership.ReceiverOwned;
                        break;
                    case BufferOwnership.PartiallyConsumed:
                        var page = state.Page
                            ?? throw new InvalidOperationException("A partially consumed provided buffer has no page.");
                        var pageVersion = state.PageVersion;
                        ClearState(ref state);
                        page.Unpin(pageVersion);
                        AssignFreshPage(ref state, BufferOwnership.ReceiverOwned);
                        break;
                    case BufferOwnership.FinalQueued:
                        throw new InvalidOperationException("A final provided-buffer segment remained queued after draining.");
                    default:
                        throw new InvalidOperationException("A provided buffer has an invalid ownership state.");
                }
            }

            _needsRearm = true;
        }
    }

    private void CompleteCancellation(int result)
    {
        lock (_stateLock)
        {
            if (result != 0 && result != -2 && result != -114)
            {
                _stopCompletion?.TrySetException(new InvalidOperationException($"Unable to cancel the multishot receive. errno={-result}"));
            }
            else if (!IsPending)
            {
                _stopCompletion?.TrySetResult();
            }
        }
    }

    private void CompleteCancellation(Exception error)
    {
        lock (_stateLock)
        {
            _stopCompletion?.TrySetException(error);
        }
    }

    private void CompleteWaitingReceive()
    {
        if (!_receiveWaiting || _receiveWriter is not { } writer)
        {
            return;
        }

        var appended = AppendReceivedPages(writer);
        if (appended > 0)
        {
            BytesTransferred = appended;
            SocketError = SocketError.Success;
            Error = null;
            _receiveWriter = null;
            _receiveWaiting = false;
            _receiveWaiter.SetResult();
        }
        else if (_terminalError is { } error)
        {
            Error = error;
            SocketError = error is SocketException socketException
                ? socketException.SocketErrorCode
                : SocketError.SocketError;
            BytesTransferred = 0;
            _receiveWriter = null;
            _receiveWaiting = false;
            _receiveWaiter.SetException(error);
        }
        else if (_finished && !_needsRearm)
        {
            BytesTransferred = 0;
            SocketError = SocketError.Success;
            Error = null;
            _receiveWriter = null;
            _receiveWaiting = false;
            _receiveWaiter.SetResult();
        }
    }

    private int AppendReceivedPages(ArcBufferWriter writer)
    {
        var appended = 0;
        while (_receivedSegments.TryPeek(out var received))
        {
            ref var state = ref _buffers[received.BufferId];
            if (state.Generation != received.Generation
                || !ReferenceEquals(state.Page, received.Page)
                || state.PageVersion != received.PageVersion)
            {
                throw new InvalidOperationException("A received segment refers to a stale provided-buffer generation.");
            }

            if (received.Offset == 0)
            {
                if (state.Adopted)
                {
                    throw new InvalidOperationException("A provided-buffer page was adopted more than once.");
                }

                writer.AppendReceivedPage(received.Page, received.Length);
                state.Adopted = true;
                _firstAdoptedPage ??= received.Page;
                _adoptedPageCount++;
            }
            else
            {
                if (!state.Adopted)
                {
                    throw new InvalidOperationException("A later provided-buffer segment arrived before its page was adopted.");
                }

                writer.AdvanceReceivedPage(received.Page, received.Offset, received.Length);
            }

            _receivedSegments.Dequeue();
            appended = checked(appended + received.Length);
            if (received.FinalForBuffer)
            {
                if (state.Ownership != BufferOwnership.FinalQueued
                    || state.CompletedOffset != received.Offset + received.Length)
                {
                    throw new InvalidOperationException("A final provided-buffer segment has inconsistent ownership state.");
                }

                state.Page = null;
                state.PageVersion = 0;
                state.CompletedOffset = 0;
                state.Adopted = false;
                state.Ownership = BufferOwnership.Free;
                received.Page.Unpin(received.PageVersion);
            }
        }

        return appended;
    }

    private void RefillFreeBuffers()
    {
        ulong addedBufferMask = 0;
        for (var i = 0; i < BufferCount; i++)
        {
            ref var state = ref _buffers[i];
            if (state.Ownership == BufferOwnership.Free)
            {
                AssignFreshPage(ref state, BufferOwnership.PendingPublication);
                addedBufferMask |= 1UL << i;
                _replacementPageCount++;
            }
        }

        if (addedBufferMask == 0 || _bufferRing == IntPtr.Zero)
        {
            return;
        }

        _pendingRefillMask |= addedBufferMask;
        try
        {
            if (Interlocked.Exchange(ref _refillQueued, 1) == 0)
            {
                Engine.EnqueueBufferRefill(this);
            }
        }
        catch
        {
            _pendingRefillMask &= ~addedBufferMask;
            Volatile.Write(ref _refillQueued, 0);
            for (var bufferId = 0; bufferId < BufferCount; bufferId++)
            {
                if ((addedBufferMask & (1UL << bufferId)) == 0)
                {
                    continue;
                }

                ref var state = ref _buffers[bufferId];
                var page = state.Page!;
                var pageVersion = state.PageVersion;
                ClearState(ref state);
                page.Unpin(pageVersion);
            }

            throw;
        }
    }

    internal void PublishPendingBuffers()
    {
        lock (_stateLock)
        {
            var bufferMask = _pendingRefillMask;
            _pendingRefillMask = 0;
            Volatile.Write(ref _refillQueued, 0);
            if (_bufferRing == IntPtr.Zero)
            {
                return;
            }

            var ring = (LinuxIoUringEngine.IoUringBufferRing*)_bufferRing;
            var added = 0;
            for (var bufferId = 0; bufferId < BufferCount; bufferId++)
            {
                if ((bufferMask & (1UL << bufferId)) == 0)
                {
                    continue;
                }

                var state = _buffers[bufferId];
                var page = state.Page
                    ?? throw new InvalidOperationException("A provided-buffer page was detached before publication.");
                if (state.Ownership != BufferOwnership.PendingPublication)
                {
                    throw new InvalidOperationException("A provided-buffer page has invalid publication ownership.");
                }

                AddBuffer(ring, page, bufferId, added++);
            }

            LinuxIoUringEngine.AdvanceBufferRing(ring, added);
            for (var bufferId = 0; bufferId < BufferCount; bufferId++)
            {
                if ((bufferMask & (1UL << bufferId)) != 0)
                {
                    _buffers[bufferId].Ownership = BufferOwnership.Published;
                }
            }
        }
    }

    private static void AddBuffer(
        LinuxIoUringEngine.IoUringBufferRing* ring,
        ArcBufferPage page,
        int bufferId,
        int offset)
    {
        var buffers = (LinuxIoUringEngine.IoUringBuffer*)ring;
        var buffer = &buffers[(ring->Tail + offset) & (BufferCount - 1)];
        buffer->Address = (ulong)Marshal.UnsafeAddrOfPinnedArrayElement(page.Array, 0);
        buffer->Length = checked((uint)page.Array.Length);
        buffer->Id = checked((ushort)bufferId);
    }

    private void Finish(Exception? error = null, bool needsRearm = false, bool stopSucceeded = false)
    {
        if (_finished)
        {
            return;
        }

        _finished = true;
        _needsRearm = needsRearm;
        _terminalError = error;
        RetireSubmission();
        if (_stopCompletion is { } stopCompletion)
        {
            if (error is null || stopSucceeded)
            {
                stopCompletion.TrySetResult();
            }
            else
            {
                stopCompletion.TrySetException(error);
            }
        }
    }

    private void Fail(Exception error)
    {
        _terminalError = error;
        _finished = true;
        _needsRearm = false;
        RetireSubmission();
        CompleteWaitingReceive();
        _stopCompletion?.TrySetException(error);
    }

    private static ArcBufferPage RentPage()
    {
        var page = ArcBufferPagePool.Shared.Rent(BufferSize);
        if (!page.IsMinimumSize || page.ReferenceCount != 0)
        {
            throw new InvalidOperationException("The provided-buffer pool returned an invalid page.");
        }

        return page;
    }

    private static void AssignFreshPage(ref BufferState state, BufferOwnership ownership)
    {
        var page = RentPage();
        var pageVersion = page.Version;
        page.Pin(pageVersion);
        state.Page = page;
        state.PageVersion = pageVersion;
        state.CompletedOffset = 0;
        state.Adopted = false;
        state.Generation = unchecked(state.Generation + 1);
        if (state.Generation == 0)
        {
            state.Generation = 1;
        }

        state.Ownership = ownership;
    }

    private void ReleaseReceiverPages()
    {
        _pendingRefillMask = 0;
        Volatile.Write(ref _refillQueued, 0);
        _receivedSegments.Clear();
        for (var i = 0; i < BufferCount; i++)
        {
            ref var state = ref _buffers[i];
            if (state.Page is not { } page)
            {
                continue;
            }

            var pageVersion = state.PageVersion;
            ClearState(ref state);
            page.Unpin(pageVersion);
        }
    }

    private static void ClearState(ref BufferState state)
    {
        state.Page = null;
        state.PageVersion = 0;
        state.CompletedOffset = 0;
        state.Adopted = false;
        state.Ownership = BufferOwnership.Free;
    }

    private void ReleaseBufferGroup()
    {
        if (!_bufferGroupReleased)
        {
            _bufferGroupReleased = true;
            Engine.ReleaseBufferGroup(_bufferGroup);
        }
    }

    private void UnregisterBufferRing()
    {
        if (_bufferRing == IntPtr.Zero)
        {
            return;
        }

        Engine.RunOnEngineThread(() =>
        {
            if (_bufferRing != IntPtr.Zero)
            {
                var result = LinuxIoUringEngine.Native.FreeBufRing(ref Engine.Ring, _bufferRing, BufferCount, _bufferGroup);
                if (result < 0)
                {
                    throw new InvalidOperationException($"Unable to free the io_uring provided buffer ring. errno={-result}");
                }

                _bufferRing = IntPtr.Zero;
            }
        });
    }

    [SuppressMessage(
        "Reliability",
        "CA2012",
        Justification = "Multishot completion is consumed by the io_uring engine and delivered through the receiver's pending read.")]
    private void StartReceive(Socket socket)
    {
        if (IsPending)
        {
            throw new InvalidOperationException("A multishot receive is already active.");
        }

        _socketForRearm = socket;
        _finished = false;
        _needsRearm = false;
        _terminalError = null;
        _stopping = false;
        _stopCompletion = null;
        _receiveStartCount++;
        BeginPreparation();
        try
        {
            SubmitPrepared(
                checked((int)socket.Handle),
                IntPtr.Zero,
                0,
                LinuxIoUringEngine.ReceiveOperation,
                waitForNotification: false);
        }
        catch
        {
            _socketForRearm = null;
            throw;
        }
    }

    private enum BufferOwnership : byte
    {
        Free,
        ReceiverOwned,
        PendingPublication,
        Published,
        PartiallyConsumed,
        FinalQueued,
    }

    private struct BufferState
    {
        internal ArcBufferPage? Page;
        internal int PageVersion;
        internal int CompletedOffset;
        internal uint Generation;
        internal bool Adopted;
        internal BufferOwnership Ownership;
    }

    private readonly record struct ReceivedSegment(
        int BufferId,
        uint Generation,
        ArcBufferPage Page,
        int PageVersion,
        int Offset,
        int Length,
        bool FinalForBuffer);

    private sealed class ReceiveWaiter : IValueTaskSource
    {
        private ManualResetValueTaskSourceCore<bool> _source = new()
        {
            RunContinuationsAsynchronously = true,
        };

        public ValueTask Reset()
        {
            _source.Reset();
            return new ValueTask(this, _source.Version);
        }

        public void SetResult() => _source.SetResult(true);

        public void SetException(Exception error) => _source.SetException(error);

        public void GetResult(short token) => _source.GetResult(token);

        public ValueTaskSourceStatus GetStatus(short token) => _source.GetStatus(token);

        public void OnCompleted(
            Action<object?> continuation,
            object? state,
            short token,
            ValueTaskSourceOnCompletedFlags flags)
            => _source.OnCompleted(continuation, state, token, flags);
    }
}

internal sealed unsafe class LinuxIoUringCancelOperation : LinuxIoUringOperation
{
    private const byte AsyncCancelOperation = 14;
    private const uint AsyncCancelUserData = 1 << 4;
    private readonly ulong _target;
    private readonly Action<int> _completed;
    private readonly Action<Exception> _failed;

    [SuppressMessage(
        "Reliability",
        "CA2012",
        Justification = "Cancellation completion is delivered through the supplied callbacks and the operation disposes itself.")]
    public LinuxIoUringCancelOperation(
        LinuxIoUringEngine engine,
        ulong target,
        Action<int> completed,
        Action<Exception> failed)
        : base(engine)
    {
        _target = target;
        _completed = completed;
        _failed = failed;
        try
        {
            BeginPreparation();
            SubmitPrepared(
                fileDescriptor: -1,
                bufferAddress: (IntPtr)target,
                bufferLength: 0,
                operationCode: AsyncCancelOperation,
                waitForNotification: false);
        }
        catch
        {
            base.Dispose();
            throw;
        }
    }

    internal override void PrepareSubmission(LinuxIoUringEngine.IoUringSubmission* submission)
        => submission->OperationFlags = AsyncCancelUserData;

    internal override void HandleCompletion(int result, uint flags)
    {
        Complete(result, queueContinuation: false);
        try
        {
            _completed(result);
        }
        finally
        {
            Dispose();
        }
    }

    internal override void Complete(Exception error)
    {
        base.Complete(error);
        try
        {
            _failed(error);
        }
        finally
        {
            Dispose();
        }
    }
}

internal sealed unsafe class LinuxIoUringSocketSender : LinuxIoUringOperation, ISocketSender
{
    private const int SynchronousSendThreshold = 16 * 1024;
    private const int ZeroCopyThreshold = 16 * 1024;
    private const int MaximumScatterBuffers = 64;
    private const ushort SendZeroCopyReportUsage = 1 << 3;
    private const uint ZeroCopyCopied = 1U << 31;
    private readonly GCHandle[] _scatterPins;
    private readonly IntPtr _iovecs;
    private readonly IntPtr _messageHeader;
    private int _scatterPinCount;
    private int _consecutiveCopiedZeroCopySends;
    private int _zeroCopyDisabled;

    public LinuxIoUringSocketSender(LinuxIoUringEngine? engine = null)
        : base(engine)
    {
        try
        {
            _scatterPins = new GCHandle[MaximumScatterBuffers];
            _iovecs = (IntPtr)NativeMemory.Alloc((nuint)(MaximumScatterBuffers * sizeof(IoVector)));
            _messageHeader = (IntPtr)NativeMemory.Alloc((nuint)sizeof(MessageHeader));
        }
        catch
        {
            NativeMemory.Free((void*)_iovecs);
            base.Dispose();
            throw;
        }
    }

    public ValueTask SendAsync(
        Socket socket,
        List<ArraySegment<byte>> buffers,
        bool buffersArePinned,
        bool useZeroCopy)
    {
        if (buffers.Count == 0)
        {
            throw new ArgumentException("At least one send buffer is required.", nameof(buffers));
        }

        var totalLength = 0;
        foreach (var buffer in buffers)
        {
            totalLength += buffer.Count;
        }

        var useZeroCopyOperation = useZeroCopy
            && Volatile.Read(ref _zeroCopyDisabled) == 0
            && totalLength >= ZeroCopyThreshold;

        if (buffers.Count == 1)
        {
            if (!useZeroCopyOperation
                && TrySendSynchronously(socket, buffers[0], buffersArePinned, out var synchronousResult))
            {
                return synchronousResult;
            }

            return Submit(
                socket,
                buffers[0],
                useZeroCopyOperation ? LinuxIoUringEngine.SendZeroCopyOperation : LinuxIoUringEngine.SendOperation,
                buffersArePinned,
                waitForNotification: useZeroCopyOperation);
        }

        if (buffers.Count > MaximumScatterBuffers)
        {
            throw new ArgumentOutOfRangeException(
                nameof(buffers),
                buffers.Count,
                $"A scatter send supports at most {MaximumScatterBuffers} buffers.");
        }

        BeginPreparation();
        try
        {
            var vectors = (IoVector*)_iovecs;
            for (var i = 0; i < buffers.Count; i++)
            {
                var buffer = buffers[i];
                var array = buffer.Array ?? throw new ArgumentException("The send buffer must be array-backed.", nameof(buffers));
                var pin = GCHandle.Alloc(array, GCHandleType.Pinned);
                _scatterPins[i] = pin;
                _scatterPinCount++;
                vectors[i] = new()
                {
                    Base = IntPtr.Add(pin.AddrOfPinnedObject(), buffer.Offset),
                    Length = (nuint)buffer.Count,
                };
            }

            *(MessageHeader*)_messageHeader = new()
            {
                Vectors = _iovecs,
                VectorCount = (nuint)buffers.Count,
            };
        }
        catch
        {
            CancelPreparation();
            throw;
        }

        return SubmitPrepared(
            socket,
            _messageHeader,
            bufferLength: 1,
            useZeroCopyOperation
                ? LinuxIoUringEngine.SendMessageZeroCopyOperation
                : LinuxIoUringEngine.SendMessageOperation,
            waitForNotification: useZeroCopyOperation);
    }

    public ValueTask SendAsync(
        Socket socket,
        ReadOnlyMemory<byte> memory,
        bool bufferIsPinned,
        bool useZeroCopy)
    {
        if (!MemoryMarshal.TryGetArray(memory, out var buffer))
        {
            throw new ArgumentException("The send buffer must be array-backed.", nameof(memory));
        }

        var operation = useZeroCopy
            && Volatile.Read(ref _zeroCopyDisabled) == 0
            && buffer.Count >= ZeroCopyThreshold
            ? LinuxIoUringEngine.SendZeroCopyOperation
            : LinuxIoUringEngine.SendOperation;
        if (operation == LinuxIoUringEngine.SendOperation
            && TrySendSynchronously(socket, buffer, bufferIsPinned, out var synchronousResult))
        {
            return synchronousResult;
        }

        return Submit(
            socket,
            buffer,
            operation,
            bufferIsPinned,
            waitForNotification: operation == LinuxIoUringEngine.SendZeroCopyOperation);
    }

    private bool TrySendSynchronously(
        Socket socket,
        ArraySegment<byte> buffer,
        bool bufferIsPinned,
        out ValueTask result)
    {
        if (!bufferIsPinned || buffer.Count >= SynchronousSendThreshold)
        {
            result = default;
            return false;
        }

        BeginPreparation();
        nint sent;
        try
        {
            var address = Marshal.UnsafeAddrOfPinnedArrayElement(buffer.Array!, buffer.Offset);
            do
            {
                sent = LinuxIoUringEngine.Native.Send(
                    checked((int)socket.Handle),
                    (void*)address,
                    checked((nuint)buffer.Count),
                    LinuxIoUringEngine.MessageDontWait | LinuxIoUringEngine.MessageNoSignal);
            }
            while (sent < 0 && Marshal.GetLastPInvokeError() == 4);
        }
        catch
        {
            CancelPreparation();
            throw;
        }

        if (sent < 0)
        {
            var errorCode = Marshal.GetLastPInvokeError();
            if (errorCode == 11)
            {
                result = SubmitPinnedFromPreparation(
                    socket,
                    buffer,
                    LinuxIoUringEngine.SendOperation,
                    waitForNotification: false);
                return true;
            }

            var error = CompleteSynchronousPreparation(-errorCode);
            result = ValueTask.FromException(error!);
            return true;
        }

        _ = CompleteSynchronousPreparation(checked((int)sent));
        result = default;
        return true;
    }

    public override void Dispose()
    {
        base.Dispose();
        NativeMemory.Free((void*)_iovecs);
        NativeMemory.Free((void*)_messageHeader);
    }

    protected override void ReleaseOperationBuffers()
    {
        base.ReleaseOperationBuffers();
        for (var i = 0; i < _scatterPinCount; i++)
        {
            _scatterPins[i].Free();
            _scatterPins[i] = default;
        }

        _scatterPinCount = 0;
    }

    internal override void PrepareSubmission(LinuxIoUringEngine.IoUringSubmission* submission)
    {
        if (WaitForNotification)
        {
            submission->IoPriority |= SendZeroCopyReportUsage;
        }
    }

    internal override void SetZeroCopyUsage(int result)
    {
        if (((uint)result & ZeroCopyCopied) == 0)
        {
            Volatile.Write(ref _consecutiveCopiedZeroCopySends, 0);
        }
        else if (++_consecutiveCopiedZeroCopySends >= 4)
        {
            Volatile.Write(ref _zeroCopyDisabled, 1);
        }
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoVector
    {
        internal IntPtr Base;
        internal nuint Length;
    }

    [StructLayout(LayoutKind.Explicit, Size = 56)]
    private struct MessageHeader
    {
        [FieldOffset(0)] private IntPtr _name;
        [FieldOffset(8)] private uint _nameLength;
        [FieldOffset(16)] internal IntPtr Vectors;
        [FieldOffset(24)] internal nuint VectorCount;
        [FieldOffset(32)] private IntPtr _control;
        [FieldOffset(40)] private nuint _controlLength;
        [FieldOffset(48)] private int _flags;
    }
}

internal abstract class LinuxIoUringOperation : IValueTaskSource, IDisposable
{
    private const int StateIdle = 0;
    private const int StatePreparing = 1;
    private const int StatePending = 2;
    private const int StateCompleting = 3;
    private const int StateDisposed = 4;
    private static readonly Action<object?> ContinuationCompleted = static _ => { };
    private readonly LinuxIoUringEngine _engine;
    private readonly uint _slotToken;
    private Action<object?>? _continuation;
    private object? _continuationState;
    private Socket? _socket;
    private int _fileDescriptor = -1;
    private byte[]? _buffer;
    private GCHandle _bufferPin;
    private int _bufferOffset;
    private int _bufferLength;
    private IntPtr _bufferAddress;
    private bool _waitForNotification;
    private int _primaryResult;
    private int _state;
    private ulong _generation;
    private ulong _userData;

    protected LinuxIoUringOperation(LinuxIoUringEngine? engine = null)
    {
        _engine = engine ?? LinuxIoUringEngine.GetNext();
        (_slotToken, _generation) = _engine.Register(this);
    }

    public int BytesTransferred { get; protected set; }

    public SocketError SocketError { get; protected set; }

    public Exception? Error { get; protected set; }

    [MemberNotNullWhen(true, nameof(Error))]
    public bool HasError => Error is not null;

    internal int FileDescriptor => _fileDescriptor;

    internal LinuxIoUringEngine Engine => _engine;

    internal IntPtr BufferAddress
        => _bufferAddress;

    internal int BufferLength => _bufferLength;

    internal byte OperationCode { get; private set; }

    internal bool WaitForNotification => _waitForNotification;

    internal int PrimaryResult => _primaryResult;

    internal uint SlotToken => _slotToken;

    internal ulong UserData => Volatile.Read(ref _userData);

    internal ulong Generation => _generation;

    internal bool IsPending => Volatile.Read(ref _state) == StatePending;

    internal virtual bool IsMultishot => false;

    internal virtual void PrepareSubmission(LinuxIoUringEngine.IoUringSubmission* submission)
    {
    }

    internal virtual void OnEngineShutdown()
    {
    }

    internal virtual void OnEngineShutdownComplete()
    {
    }

    internal virtual void SetZeroCopyUsage(int result)
    {
    }

    protected ValueTask Submit(
        Socket socket,
        ArraySegment<byte> buffer,
        byte operationCode,
        bool bufferIsPinned,
        bool waitForNotification = false)
    {
        Debug.Assert(_socket is null);
        Debug.Assert(_buffer is null);
        Debug.Assert(!_bufferPin.IsAllocated);
        Debug.Assert(_continuation is null);

        BeginPreparation();
        try
        {
            _buffer = buffer.Array ?? throw new ArgumentException("The I/O buffer must be array-backed.", nameof(buffer));
            _bufferOffset = buffer.Offset;
            _bufferLength = buffer.Count;
            if (bufferIsPinned)
            {
                _bufferAddress = Marshal.UnsafeAddrOfPinnedArrayElement(_buffer, _bufferOffset);
            }
            else
            {
                _bufferPin = GCHandle.Alloc(_buffer, GCHandleType.Pinned);
                _bufferAddress = IntPtr.Add(_bufferPin.AddrOfPinnedObject(), _bufferOffset);
            }
        }
        catch
        {
            CancelPreparation();
            throw;
        }

        return SubmitPrepared(socket, _bufferAddress, _bufferLength, operationCode, waitForNotification);
    }

    protected ValueTask SubmitPinnedFromPreparation(
        Socket socket,
        ArraySegment<byte> buffer,
        byte operationCode,
        bool waitForNotification)
    {
        Debug.Assert(Volatile.Read(ref _state) == StatePreparing);
        try
        {
            _buffer = buffer.Array ?? throw new ArgumentException("The I/O buffer must be array-backed.", nameof(buffer));
            _bufferOffset = buffer.Offset;
            _bufferLength = buffer.Count;
            _bufferAddress = Marshal.UnsafeAddrOfPinnedArrayElement(_buffer, _bufferOffset);
        }
        catch
        {
            CancelPreparation();
            throw;
        }

        return SubmitPrepared(socket, _bufferAddress, _bufferLength, operationCode, waitForNotification);
    }

    protected ValueTask SubmitPrepared(
        Socket socket,
        IntPtr bufferAddress,
        int bufferLength,
        byte operationCode,
        bool waitForNotification)
        => SubmitPrepared(
            checked((int)socket.Handle),
            bufferAddress,
            bufferLength,
            operationCode,
            waitForNotification,
            socket);

    protected ValueTask SubmitPrepared(
        int fileDescriptor,
        IntPtr bufferAddress,
        int bufferLength,
        byte operationCode,
        bool waitForNotification)
        => SubmitPrepared(
            fileDescriptor,
            bufferAddress,
            bufferLength,
            operationCode,
            waitForNotification,
            socket: null);

    private ValueTask SubmitPrepared(
        int fileDescriptor,
        IntPtr bufferAddress,
        int bufferLength,
        byte operationCode,
        bool waitForNotification,
        Socket? socket)
    {
        try
        {
            _socket = socket;
            _fileDescriptor = fileDescriptor;
            _bufferAddress = bufferAddress;
            _bufferLength = bufferLength;
            OperationCode = operationCode;
            _waitForNotification = waitForNotification;
            _primaryResult = 0;
            BytesTransferred = 0;
            SocketError = SocketError.Success;
            Error = null;
            var generation = (_generation + 1) & LinuxIoUringEngine.UserDataGenerationMask;
            _generation = generation == 0 ? 1 : generation;
            Volatile.Write(
                ref _userData,
                (_generation << LinuxIoUringEngine.UserDataSlotBits) | _slotToken);
        }
        catch
        {
            CancelPreparation();
            throw;
        }

        if (Volatile.Read(ref _state) != StatePreparing)
        {
            throw new InvalidOperationException("The io_uring operation left the preparation state unexpectedly.");
        }

        Volatile.Write(ref _state, StatePending);
        _engine.Enqueue(this);
        return new ValueTask(this, 0);
    }

    protected void BeginPreparation()
    {
        if (Interlocked.CompareExchange(ref _state, StatePreparing, StateIdle) != StateIdle)
        {
            throw new InvalidOperationException("The io_uring operation is already active or has been disposed.");
        }
    }

    protected void CancelPreparation()
    {
        ReleaseSubmission();
        if (Volatile.Read(ref _state) != StatePreparing)
        {
            throw new InvalidOperationException("The io_uring operation left the preparation state unexpectedly.");
        }

        Volatile.Write(ref _state, StateIdle);
    }

    protected Exception? CompleteSynchronousPreparation(int result)
    {
        if (Volatile.Read(ref _state) != StatePreparing)
        {
            throw new InvalidOperationException("The io_uring operation left the preparation state unexpectedly.");
        }

        Exception? error;
        ReleaseSubmission();
        if (result >= 0)
        {
            BytesTransferred = result;
            SocketError = SocketError.Success;
            Error = error = null;
        }
        else
        {
            BytesTransferred = 0;
            SocketError = MapSocketError(-result);
            Error = error = new SocketException((int)SocketError);
        }

        Volatile.Write(ref _state, StateIdle);
        return error;
    }

    internal void SetPrimaryResult(int result) => _primaryResult = result;

    internal virtual void HandleCompletion(int result, uint flags)
    {
        Complete(result, result >= 0);
    }

    internal virtual void Complete(int result, bool queueContinuation)
    {
        if (Interlocked.CompareExchange(ref _state, StateCompleting, StatePending) != StatePending)
        {
            throw new InvalidOperationException("The io_uring operation received an unexpected completion.");
        }

        ReleaseSubmission();
        if (result >= 0)
        {
            BytesTransferred = result;
            SocketError = SocketError.Success;
            Error = null;
        }
        else
        {
            BytesTransferred = 0;
            SocketError = MapSocketError(-result);
            Error = new SocketException((int)SocketError);
        }

        Volatile.Write(ref _state, StateIdle);
        SignalCompletion(queueContinuation);
    }

    internal virtual void Complete(Exception error)
    {
        if (Interlocked.CompareExchange(ref _state, StateCompleting, StatePending) != StatePending)
        {
            return;
        }

        ReleaseSubmission();
        BytesTransferred = 0;
        SocketError = SocketError.SocketError;
        Error = error;
        Volatile.Write(ref _state, StateIdle);
        SignalCompletion(queueContinuation: false);
    }

    public void GetResult(short token)
    {
        _continuation = null;
        if (Error is { } error)
        {
            throw error;
        }
    }

    public ValueTaskSourceStatus GetStatus(short token)
        => !ReferenceEquals(_continuation, ContinuationCompleted)
            ? ValueTaskSourceStatus.Pending
            : Error is null
                ? ValueTaskSourceStatus.Succeeded
                : ValueTaskSourceStatus.Faulted;

    public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
    {
        _continuationState = state;
        var previous = Interlocked.CompareExchange(ref _continuation, continuation, null);
        if (ReferenceEquals(previous, ContinuationCompleted))
        {
            _continuationState = null;
            continuation(state);
        }
    }

    public virtual void Dispose()
    {
        if (Interlocked.CompareExchange(ref _state, StateDisposed, StateIdle) != StateIdle)
        {
            throw new InvalidOperationException("An active io_uring operation cannot be disposed.");
        }

        Debug.Assert(_socket is null);
        Debug.Assert(_buffer is null);
        Debug.Assert(!_bufferPin.IsAllocated);
        _engine.Unregister(this);
    }

    protected void RetireSubmission()
    {
        if (Interlocked.CompareExchange(ref _state, StateIdle, StatePending) != StatePending)
        {
            throw new InvalidOperationException("The io_uring operation received an unexpected final completion.");
        }

        ReleaseSubmission();
    }

    private void ReleaseSubmission()
    {
        _socket = null;
        _fileDescriptor = -1;
        _bufferOffset = 0;
        _bufferLength = 0;
        _bufferAddress = IntPtr.Zero;
        OperationCode = 0;
        _waitForNotification = false;
        _primaryResult = 0;
        ReleaseOperationBuffers();
    }

    protected virtual void ReleaseOperationBuffers()
    {
        _buffer = null;
        if (_bufferPin.IsAllocated)
        {
            _bufferPin.Free();
        }
    }

    private static SocketError MapSocketError(int errno) => LinuxIoUringEngine.MapSocketErrorCode(errno);

    private void SignalCompletion(bool queueContinuation)
    {
        var continuation = _continuation;
        if (continuation is not null
            || (continuation = Interlocked.CompareExchange(ref _continuation, ContinuationCompleted, null)) is not null)
        {
            var state = _continuationState;
            _continuationState = null;
            _continuation = ContinuationCompleted;
            if (queueContinuation)
            {
                ThreadPool.UnsafeQueueUserWorkItem(continuation, state, preferLocal: false);
            }
            else
            {
                continuation(state);
            }
        }
    }
}

[SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "Engines are process-lifetime singletons backed by background threads.")]
internal sealed unsafe partial class LinuxIoUringEngine
{
    internal const byte SendOperation = 26;
    internal const byte ReceiveOperation = 27;
    internal const byte SendMessageOperation = 9;
    internal const byte ReceiveMessageOperation = 10;
    internal const byte SendZeroCopyOperation = 47;
    internal const byte SendMessageZeroCopyOperation = 48;
    internal const byte BufferSelect = 1 << 5;
    internal const uint ReceiveMultishot = 1 << 1;
    internal const uint CompletionHasBuffer = 1;
    internal const uint CompletionBufferMore = 1 << 4;
    internal const int MessageDontWait = 0x40;
    internal const int MessageNoSignal = 0x4000;

    private const byte PollOperation = 6;
    private const uint PollIn = 1;
    internal const uint CompletionHasMore = 1 << 1;
    internal const uint CompletionHasSocketData = 1 << 2;
    internal const uint CompletionIsNotification = 1 << 3;
    private const uint SetupSubmitAll = 1 << 7;
    private const uint SetupCooperativeTaskRun = 1 << 8;
    private const uint SetupSingleIssuer = 1 << 12;
    private const uint SetupDeferTaskRun = 1 << 13;
    private const ulong WakeUserData = 1;
    private const int EventFdCloseOnExec = 0x80000;
    private const int EventFdNonBlocking = 0x800;
    private const int ErrorBusy = 16;
    private const int InitialOperationCapacity = 256;
    // Preserve low latency for small completions while keeping batched I/O work off the ring thread.
    private const int QueueContinuationThreshold = 4 * 1024;
    private const int TargetProcessorsPerEngine = 4;
    private const int MaximumEngineCount = 4;
    internal const int UserDataSlotBits = 20;
    internal const ulong UserDataSlotMask = (1UL << UserDataSlotBits) - 1;
    internal const ulong UserDataGenerationMask = ulong.MaxValue >> UserDataSlotBits;

    private static readonly Lazy<LinuxIoUringEngine>[] Engines = CreateEngines();
    private static int _nextEngine = -1;
    private readonly ConcurrentQueue<LinuxIoUringSocketMultishotReceiver> _bufferRefills = new();
    private readonly ConcurrentQueue<LinuxIoUringOperation> _pending = new();
    private readonly object _bufferGroupsLock = new();
    private readonly object _operationsLock = new();
    private readonly Stack<ushort> _freeBufferGroups = [];
    private readonly Stack<int> _freeOperationSlots = [];
    private readonly ManualResetEventSlim _started = new(initialState: false);
    private readonly int _engineId;
    private LinuxIoUringOperation?[] _operations = new LinuxIoUringOperation[InitialOperationCapacity];
    private ulong[] _operationGenerations = new ulong[InitialOperationCapacity];
    private int _actionEnqueuers;
    private int _operationCount;
    private IoUring _ring;
    private Exception? _fatalError;
    private int _enqueuers;
    private int _eventFd = -1;
    private int _engineThreadId;
    private bool _ringInitialized;
    private long _zeroCopyPrimaryCompletions;
    private long _zeroCopyNotificationCompletions;
    private long _zeroCopyFallbackCompletions;
    private long _zeroCopyCopiedCompletions;
    private bool _wakePollArmed;
    private readonly ConcurrentQueue<EngineAction> _actions = new();
    private int _nextBufferGroup = 1;

    private LinuxIoUringEngine(int engineId)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("The io_uring transport requires Linux.");
        }

        if (!Environment.Is64BitProcess || !BitConverter.IsLittleEndian)
        {
            throw new PlatformNotSupportedException("The io_uring transport requires a little-endian 64-bit process.");
        }

        _engineId = engineId;
        var thread = new Thread(Run)
        {
            IsBackground = true,
            Name = $"Orleans io_uring {engineId}",
        };
        thread.Start();
        _started.Wait();
        if (Volatile.Read(ref _fatalError) is { } error)
        {
            throw new InvalidOperationException("Unable to start the Orleans io_uring engine.", error);
        }
    }

    internal static LinuxIoUringEngine GetNext()
    {
        var index = (uint)Interlocked.Increment(ref _nextEngine) % (uint)Engines.Length;
        return Engines[index].Value;
    }

    internal static bool IsRequested
        => OperatingSystem.IsLinux()
            && (string.Equals(
                    Environment.GetEnvironmentVariable("ORLEANS_USE_IO_URING"),
                    "1",
                    StringComparison.Ordinal)
                || AppContext.TryGetSwitch(
                    "Orleans.Connections.Transport.Sockets.UseIoUring",
                    out var enabled)
                && enabled);

    internal ref IoUring Ring => ref _ring;

    internal bool IsShuttingDown => Volatile.Read(ref _fatalError) is not null;

    internal bool IsEngineThread => Environment.CurrentManagedThreadId == Volatile.Read(ref _engineThreadId);

    internal void EnqueueAction(Action action)
    {
        if (IsEngineThread)
        {
            action();
            return;
        }

        Interlocked.Increment(ref _actionEnqueuers);
        try
        {
            if (Volatile.Read(ref _fatalError) is { } error)
            {
                throw new InvalidOperationException("The io_uring engine is not available.", error);
            }

            _actions.Enqueue(new EngineAction(action, completion: null));
            Wake();
        }
        finally
        {
            Interlocked.Decrement(ref _actionEnqueuers);
        }
    }

    internal void EnqueueBufferRefill(LinuxIoUringSocketMultishotReceiver receiver)
    {
        if (IsEngineThread)
        {
            receiver.PublishPendingBuffers();
            return;
        }

        Interlocked.Increment(ref _actionEnqueuers);
        try
        {
            if (Volatile.Read(ref _fatalError) is { } error)
            {
                throw new InvalidOperationException("The io_uring engine is not available.", error);
            }

            _bufferRefills.Enqueue(receiver);
            Wake();
        }
        finally
        {
            Interlocked.Decrement(ref _actionEnqueuers);
        }
    }

    internal void RunOnEngineThread(Action action)
    {
        if (IsEngineThread)
        {
            action();
            return;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.Increment(ref _actionEnqueuers);
        try
        {
            if (Volatile.Read(ref _fatalError) is { } error)
            {
                throw new InvalidOperationException("The io_uring engine is not available.", error);
            }

            _actions.Enqueue(new EngineAction(action, completion));
            Wake();
        }
        finally
        {
            Interlocked.Decrement(ref _actionEnqueuers);
        }

        completion.Task.GetAwaiter().GetResult();
    }

    internal static LinuxIoUringEngine GetOther(LinuxIoUringEngine engine)
        => Engines.Length == 1
            ? engine
            : Engines[(engine._engineId + 1) % Engines.Length].Value;

    internal static (long Primary, long Notifications, long Fallbacks, long Copied) GetZeroCopyStatistics()
    {
        long primary = 0;
        long notifications = 0;
        long fallbacks = 0;
        long copied = 0;
        foreach (var engine in Engines)
        {
            if (engine.IsValueCreated)
            {
                primary += Volatile.Read(ref engine.Value._zeroCopyPrimaryCompletions);
                notifications += Volatile.Read(ref engine.Value._zeroCopyNotificationCompletions);
                fallbacks += Volatile.Read(ref engine.Value._zeroCopyFallbackCompletions);
                copied += Volatile.Read(ref engine.Value._zeroCopyCopiedCompletions);
            }
        }

        return (primary, notifications, fallbacks, copied);
    }

    internal ushort AllocateBufferGroup()
    {
        lock (_bufferGroupsLock)
        {
            if (_freeBufferGroups.TryPop(out var bufferGroup))
            {
                return bufferGroup;
            }

            if (_nextBufferGroup > ushort.MaxValue)
            {
                throw new InvalidOperationException("The io_uring engine has no available provided-buffer groups.");
            }

            return checked((ushort)_nextBufferGroup++);
        }
    }

    internal void ReleaseBufferGroup(ushort bufferGroup)
    {
        lock (_bufferGroupsLock)
        {
            _freeBufferGroups.Push(bufferGroup);
        }
    }

    internal (uint SlotToken, ulong Generation) Register(LinuxIoUringOperation operation)
    {
        lock (_operationsLock)
        {
            int index;
            if (!_freeOperationSlots.TryPop(out index))
            {
                index = _operationCount++;
                if ((ulong)index > UserDataSlotMask - 2)
                {
                    throw new InvalidOperationException("The io_uring engine has no available operation slots.");
                }

                if (index == _operations.Length)
                {
                    var resized = new LinuxIoUringOperation[checked(index * 2)];
                    var resizedGenerations = new ulong[resized.Length];
                    Array.Copy(_operations, resized, index);
                    Array.Copy(_operationGenerations, resizedGenerations, index);
                    Volatile.Write(ref _operations, resized);
                    Volatile.Write(ref _operationGenerations, resizedGenerations);
                }
            }

            Volatile.Write(ref _operations[index], operation);
            return (checked((uint)index + 2), _operationGenerations[index]);
        }
    }

    internal void Unregister(LinuxIoUringOperation operation)
    {
        var index = checked((int)operation.SlotToken - 2);
        lock (_operationsLock)
        {
            var operations = Volatile.Read(ref _operations);
            if (!ReferenceEquals(Volatile.Read(ref operations[index]), operation))
            {
                throw new InvalidOperationException("The io_uring operation is not registered with this engine.");
            }

            _operationGenerations[index] = operation.Generation;
            Volatile.Write(ref operations[index], null);
            _freeOperationSlots.Push(index);
        }
    }

    private static Lazy<LinuxIoUringEngine>[] CreateEngines()
    {
        var count = Math.Clamp(
            (Environment.ProcessorCount + TargetProcessorsPerEngine - 1) / TargetProcessorsPerEngine,
            1,
            MaximumEngineCount);
        var result = new Lazy<LinuxIoUringEngine>[count];
        for (var i = 0; i < result.Length; i++)
        {
            var engineId = i;
            result[i] = new(() => new LinuxIoUringEngine(engineId));
        }

        return result;
    }

    public void Enqueue(LinuxIoUringOperation operation)
    {
        Interlocked.Increment(ref _enqueuers);
        if (Volatile.Read(ref _fatalError) is { } error)
        {
            Interlocked.Decrement(ref _enqueuers);
            operation.Complete(error);
            return;
        }

        _pending.Enqueue(operation);
        if (Environment.CurrentManagedThreadId == Volatile.Read(ref _engineThreadId))
        {
            Interlocked.Decrement(ref _enqueuers);
            return;
        }

        Wake();
        Interlocked.Decrement(ref _enqueuers);
    }

    private void Wake()
    {
        ulong value = 1;
        while (Native.Write(_eventFd, &value, sizeof(ulong)) < 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            if (errno == 4)
            {
                continue;
            }

            if (errno != 11)
            {
                var wakeError = new InvalidOperationException($"Unable to wake the io_uring engine. errno={errno}");
                Volatile.Write(ref _fatalError, wakeError);
                var eventFd = Interlocked.Exchange(ref _eventFd, -1);
                if (eventFd >= 0)
                {
                    Native.Close(eventFd);
                }
            }

            break;
        }
    }

    private void Run()
    {
        try
        {
            Volatile.Write(ref _engineThreadId, Environment.CurrentManagedThreadId);
            var flags = SetupSubmitAll | SetupSingleIssuer | SetupDeferTaskRun | SetupCooperativeTaskRun;
            ThrowIfError(Native.QueueInit(4096, ref _ring, flags));
            _ringInitialized = true;
            if (GetNapiBusyPollTimeout() is { } busyPollTimeout)
            {
                var napi = new IoUringNapi
                {
                    BusyPollTimeout = busyPollTimeout,
                    PreferBusyPoll = 1,
                };
                ThrowIfError(Native.RegisterNapi(ref _ring, ref napi));
            }

            _eventFd = Native.EventFd(0, EventFdCloseOnExec | EventFdNonBlocking);
            if (_eventFd < 0)
            {
                throw new InvalidOperationException($"Unable to create the io_uring eventfd. errno={Marshal.GetLastPInvokeError()}");
            }

            ArmWakePoll();
            _started.Set();

            while (true)
            {
                while (_bufferRefills.TryDequeue(out var receiver))
                {
                    receiver.PublishPendingBuffers();
                }

                while (_actions.TryDequeue(out var action))
                {
                    action.Execute();
                }

                SubmitPending();
                if (!HasCompletions())
                {
                    SubmitAndWait();
                }

                DrainCompletions();
            }
        }
        catch (Exception error)
        {
            List<LinuxIoUringOperation> failedOperations = [];
            Volatile.Write(ref _fatalError, error);
            var spinner = new SpinWait();
            while (Volatile.Read(ref _enqueuers) != 0)
            {
                spinner.SpinOnce();
            }

            while (Volatile.Read(ref _actionEnqueuers) != 0)
            {
                spinner.SpinOnce();
            }

            while (_pending.TryDequeue(out _))
            {
            }

            while (_bufferRefills.TryDequeue(out _))
            {
            }

            while (_actions.TryDequeue(out var action))
            {
                action.Fail(error);
            }

            var operations = Volatile.Read(ref _operations);
            foreach (var operation in operations)
            {
                if (operation is { IsPending: true })
                {
                    failedOperations.Add(operation);
                }
            }

            if (_ringInitialized)
            {
                foreach (var operation in operations)
                {
                    operation?.OnEngineShutdown();
                }

                _ringInitialized = false;
                Native.QueueExit(ref _ring);
                foreach (var operation in operations)
                {
                    operation?.OnEngineShutdownComplete();
                }
            }

            if (_eventFd >= 0)
            {
                Native.Close(_eventFd);
                _eventFd = -1;
            }

            _started.Set();
            foreach (var operation in failedOperations)
            {
                operation.Complete(error);
            }
        }
    }

    private void SubmitPending()
    {
        bool repeat;
        do
        {
            repeat = false;
            var submitted = 0;
            while (_pending.TryDequeue(out var operation))
            {
                IoUringSubmission* sqe;
                try
                {
                    sqe = GetSubmission();
                }
                catch
                {
                    _pending.Enqueue(operation);
                    throw;
                }

                *sqe = default;
                sqe->Opcode = operation.OperationCode;
                sqe->FileDescriptor = operation.FileDescriptor;
                sqe->Address = (ulong)operation.BufferAddress;
                sqe->Length = checked((uint)operation.BufferLength);
                sqe->UserData = operation.UserData;
                operation.PrepareSubmission(sqe);

                submitted++;
                if (submitted == 1024)
                {
                    Submit();
                    submitted = 0;
                }
            }

            if (!_wakePollArmed)
            {
                ArmWakePoll();
                submitted++;
            }

            if (submitted > 0)
            {
                repeat = Submit();
            }
        }
        while (repeat);
    }

    private void DrainCompletions()
    {
        while (TryGetCompletion(out var completion))
        {
            var userData = completion.UserData;
            var result = completion.Result;
            var flags = completion.Flags;

            if (userData == WakeUserData)
            {
                _wakePollArmed = false;
                ulong value;
                while (Native.Read(_eventFd, &value, sizeof(ulong)) < 0)
                {
                    var error = Marshal.GetLastPInvokeError();
                    if (error == 4)
                    {
                        continue;
                    }

                    if (error != 11)
                    {
                        throw new InvalidOperationException($"Unable to drain the io_uring eventfd. errno={error}");
                    }

                    break;
                }

                continue;
            }

            if (!TryGetOperation(userData, out var operation))
            {
                throw new InvalidOperationException($"io_uring completion {userData} has no owner.");
            }

            if (operation.IsMultishot)
            {
                operation.HandleCompletion(result, flags);
            }
            else if (operation.WaitForNotification)
            {
                if ((flags & CompletionIsNotification) != 0)
                {
                    _zeroCopyNotificationCompletions++;
                    operation.SetZeroCopyUsage(result);
                    if (((uint)result & (1U << 31)) != 0)
                    {
                        _zeroCopyCopiedCompletions++;
                    }

                    operation.Complete(
                        operation.PrimaryResult,
                        operation.PrimaryResult >= QueueContinuationThreshold);
                }
                else if ((flags & CompletionHasMore) == 0)
                {
                    if (result >= 0)
                    {
                        _zeroCopyFallbackCompletions++;
                        operation.SetZeroCopyUsage(unchecked((int)(1U << 31)));
                    }

                    operation.Complete(result, result >= QueueContinuationThreshold);
                }
                else
                {
                    _zeroCopyPrimaryCompletions++;
                    operation.SetPrimaryResult(result);
                }
            }
            else
            {
                operation.Complete(result, result >= QueueContinuationThreshold);
            }
        }
    }

    private bool TryGetOperation(ulong userData, [NotNullWhen(true)] out LinuxIoUringOperation? operation)
    {
        var slotToken = userData & UserDataSlotMask;
        if (slotToken < 2)
        {
            operation = null;
            return false;
        }

        var index = checked((int)slotToken - 2);
        var operations = Volatile.Read(ref _operations);
        if (index >= operations.Length)
        {
            operation = null;
            return false;
        }

        operation = Volatile.Read(ref operations[index]);
        return operation is not null && operation.UserData == userData;
    }

    private IoUringSubmission* GetSubmission()
    {
        var result = TryGetSubmission();
        if (result != null)
        {
            return result;
        }

        Submit();
        result = TryGetSubmission();
        if (result == null)
        {
            throw new InvalidOperationException("The io_uring submission queue remained full after submission.");
        }

        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private IoUringSubmission* TryGetSubmission()
    {
        ref var submission = ref _ring.Submission;
        var tail = submission.SubmissionTail;
        var next = tail + 1;
        if (next - Volatile.Read(ref *submission.KernelHead) > submission.RingEntries)
        {
            return null;
        }

        submission.SubmissionTail = next;
        return &submission.Submissions[tail & submission.RingMask];
    }

    private void ArmWakePoll()
    {
        var sqe = GetSubmission();
        *sqe = default;
        sqe->Opcode = PollOperation;
        sqe->FileDescriptor = _eventFd;
        sqe->OperationFlags = PollIn;
        sqe->UserData = WakeUserData;
        _wakePollArmed = true;
    }

    private bool TryGetCompletion(out IoUringCompletion completion)
    {
        var head = Volatile.Read(ref *_ring.Completion.KernelHead);
        var tail = Volatile.Read(ref *_ring.Completion.KernelTail);
        if (head == tail)
        {
            completion = default;
            return false;
        }

        completion = _ring.Completion.Completions[head & _ring.Completion.RingMask];
        Volatile.Write(ref *_ring.Completion.KernelHead, head + 1);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool HasCompletions()
        => Volatile.Read(ref *_ring.Completion.KernelHead)
            != Volatile.Read(ref *_ring.Completion.KernelTail);

    private static void ThrowIfError(int result)
    {
        if (result < 0)
        {
            throw new InvalidOperationException($"io_uring operation failed. errno={-result}");
        }
    }

    private static uint? GetNapiBusyPollTimeout()
        => uint.TryParse(
            Environment.GetEnvironmentVariable("ORLEANS_IO_URING_NAPI_BUSY_POLL_US"),
            out var value)
            && value > 0
                ? value
                : null;

    internal static SocketError MapSocketErrorCode(int errno) => errno switch
    {
        4 => SocketError.Interrupted,
        9 or 125 => SocketError.OperationAborted,
        11 => SocketError.WouldBlock,
        12 or 105 => SocketError.NoBufferSpaceAvailable,
        22 => SocketError.InvalidArgument,
        32 => SocketError.Shutdown,
        103 => SocketError.ConnectionAborted,
        104 => SocketError.ConnectionReset,
        107 => SocketError.NotConnected,
        110 => SocketError.TimedOut,
        111 => SocketError.ConnectionRefused,
        _ => SocketError.SocketError,
    };

    private bool Submit()
    {
        var drainedCompletions = false;
        while (true)
        {
            var result = Native.Submit(ref _ring);
            if (result == -4)
            {
                continue;
            }

            if (result == -ErrorBusy)
            {
                DrainCompletions();
                drainedCompletions = true;
                continue;
            }

            ThrowIfError(result);
            return drainedCompletions;
        }
    }

    private void SubmitAndWait()
    {
        int result;
        do
        {
            result = Native.SubmitAndWait(ref _ring, 1);
        }
        while (result == -4);

        if (result == -ErrorBusy)
        {
            return;
        }

        ThrowIfError(result);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct IoUringSubmissionQueue
    {
        internal uint* KernelHead;
        internal uint* KernelTail;
        internal uint* KernelRingMask;
        internal uint* KernelRingEntries;
        internal uint* KernelFlags;
        internal uint* KernelDropped;
        internal uint* Array;
        internal IoUringSubmission* Submissions;
        internal uint SubmissionHead;
        internal uint SubmissionTail;
        internal nuint RingSize;
        internal void* RingPointer;
        internal uint RingMask;
        internal uint RingEntries;
        internal uint SubmissionsSize;
        private uint _padding;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct IoUringCompletionQueue
    {
        internal uint* KernelHead;
        internal uint* KernelTail;
        internal uint* KernelRingMask;
        internal uint* KernelRingEntries;
        internal uint* KernelFlags;
        internal uint* KernelOverflow;
        internal IoUringCompletion* Completions;
        internal nuint RingSize;
        internal void* RingPointer;
        internal uint RingMask;
        internal uint RingEntries;
        private uint _padding0;
        private uint _padding1;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct IoUring
    {
        internal IoUringSubmissionQueue Submission;
        internal IoUringCompletionQueue Completion;
        internal uint Flags;
        internal int RingFileDescriptor;
        internal uint Features;
        internal int EnterRingFileDescriptor;
        internal byte InternalFlags;
        private byte _padding0;
        private byte _padding1;
        private byte _padding2;
        private uint _padding3;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct IoUringNapi
    {
        internal uint BusyPollTimeout;
        internal byte PreferBusyPoll;
        internal byte Opcode;
        private ushort _padding;
        internal uint OperationParameter;
        private uint _reserved;
    }

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    internal struct IoUringSubmission
    {
        [FieldOffset(0)] internal byte Opcode;
        [FieldOffset(1)] internal byte Flags;
        [FieldOffset(2)] internal ushort IoPriority;
        [FieldOffset(4)] internal int FileDescriptor;
        [FieldOffset(8)] internal ulong Offset;
        [FieldOffset(16)] internal ulong Address;
        [FieldOffset(24)] internal uint Length;
        [FieldOffset(28)] internal uint OperationFlags;
        [FieldOffset(32)] internal ulong UserData;
        [FieldOffset(40)] internal ushort BufferIndex;
        [FieldOffset(42)] internal ushort Personality;
        [FieldOffset(44)] internal int InputFileDescriptor;
        [FieldOffset(48)] internal ulong Address3;
        [FieldOffset(56)] private ulong _padding;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct IoUringCompletion
    {
        internal readonly ulong UserData;
        internal readonly int Result;
        internal readonly uint Flags;
    }

    internal static partial class Native
    {
        [LibraryImport("liburing.so.2", EntryPoint = "io_uring_queue_init")]
        internal static partial int QueueInit(uint entries, ref IoUring ring, uint flags);

        [LibraryImport("liburing.so.2", EntryPoint = "io_uring_submit")]
        internal static partial int Submit(ref IoUring ring);

        [LibraryImport("liburing.so.2", EntryPoint = "io_uring_submit_and_wait")]
        internal static partial int SubmitAndWait(ref IoUring ring, uint waitCount);

        [LibraryImport("liburing.so.2", EntryPoint = "io_uring_queue_exit")]
        internal static partial void QueueExit(ref IoUring ring);

        [LibraryImport("liburing.so.2", EntryPoint = "io_uring_setup_buf_ring")]
        internal static partial IntPtr SetupBufRing(
            ref IoUring ring,
            uint entries,
            int bufferGroup,
            uint flags,
            int* error);

        [LibraryImport("liburing.so.2", EntryPoint = "io_uring_free_buf_ring")]
        internal static partial int FreeBufRing(
            ref IoUring ring,
            IntPtr bufferRing,
            uint entries,
            int bufferGroup);

        [LibraryImport("liburing.so.2", EntryPoint = "io_uring_register_napi")]
        internal static partial int RegisterNapi(ref IoUring ring, ref IoUringNapi napi);

        [LibraryImport("libc", EntryPoint = "eventfd", SetLastError = true)]
        internal static partial int EventFd(uint initialValue, int flags);

        [LibraryImport("libc", EntryPoint = "write", SetLastError = true)]
        internal static partial nint Write(int fileDescriptor, void* buffer, nuint count);

        [LibraryImport("libc", EntryPoint = "read", SetLastError = true)]
        internal static partial nint Read(int fileDescriptor, void* buffer, nuint count);

        [LibraryImport("libc", EntryPoint = "send", SetLastError = true)]
        internal static partial nint Send(int fileDescriptor, void* buffer, nuint count, int flags);

        [LibraryImport("libc", EntryPoint = "close")]
        internal static partial int Close(int fileDescriptor);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct IoUringBuffer
    {
        internal ulong Address;
        internal uint Length;
        internal ushort Id;
        private ushort _reserved;
    }

    [StructLayout(LayoutKind.Explicit, Size = 16)]
    internal struct IoUringBufferRing
    {
        [FieldOffset(0)] internal ulong Reserved1;
        [FieldOffset(8)] internal uint Reserved2;
        [FieldOffset(12)] internal ushort Reserved3;
        [FieldOffset(14)] internal ushort Tail;
    }

    internal static void AdvanceBufferRing(IoUringBufferRing* ring, int count)
    {
        Volatile.Write(ref ring->Tail, unchecked((ushort)(ring->Tail + count)));
    }

    private sealed class EngineAction
    {
        private readonly Action _action;
        private readonly TaskCompletionSource? _completion;

        internal EngineAction(Action action, TaskCompletionSource? completion)
        {
            _action = action;
            _completion = completion;
        }

        internal void Execute()
        {
            if (_completion is null)
            {
                _action();
                return;
            }

            try
            {
                _action();
                _completion.TrySetResult();
            }
            catch (Exception error)
            {
                _completion.TrySetException(error);
            }
        }

        internal void Fail(Exception error) => _completion?.TrySetException(error);
    }
}
