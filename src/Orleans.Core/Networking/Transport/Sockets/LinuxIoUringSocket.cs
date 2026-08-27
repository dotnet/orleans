#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;

namespace Orleans.Connections.Transport.Sockets;

internal sealed unsafe class LinuxIoUringSocketReceiver : LinuxIoUringOperation, ISocketReceiver
{
    private const int MaximumScatterBuffers = 8;
    private readonly IntPtr _iovecs = (IntPtr)NativeMemory.Alloc(
        (nuint)(MaximumScatterBuffers * sizeof(IoVector)));
    private readonly IntPtr _messageHeader = (IntPtr)NativeMemory.Alloc((nuint)sizeof(MessageHeader));

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

        return SubmitPrepared(
            socket,
            _messageHeader,
            bufferLength: 1,
            LinuxIoUringEngine.ReceiveMessageOperation,
            waitForNotification: false);
    }

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

internal sealed unsafe class LinuxIoUringSocketSender : LinuxIoUringOperation, ISocketSender
{
    private const int ZeroCopyThreshold = 16 * 1024;
    private const int MaximumScatterBuffers = 64;
    private readonly GCHandle[] _scatterPins = new GCHandle[MaximumScatterBuffers];
    private readonly IntPtr _iovecs = (IntPtr)NativeMemory.Alloc(
        (nuint)(MaximumScatterBuffers * sizeof(IoVector)));
    private readonly IntPtr _messageHeader = (IntPtr)NativeMemory.Alloc((nuint)sizeof(MessageHeader));
    private int _scatterPinCount;

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

        if (!useZeroCopy || totalLength < ZeroCopyThreshold)
        {
            return Submit(socket, buffers[0], LinuxIoUringEngine.SendOperation, buffersArePinned);
        }

        if (buffers.Count == 1)
        {
            return Submit(
                socket,
                buffers[0],
                LinuxIoUringEngine.SendZeroCopyOperation,
                buffersArePinned,
                waitForNotification: true);
        }

        if (buffers.Count > MaximumScatterBuffers)
        {
            throw new ArgumentOutOfRangeException(
                nameof(buffers),
                buffers.Count,
                $"A zero-copy send supports at most {MaximumScatterBuffers} buffers.");
        }

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

        return SubmitPrepared(
            socket,
            _messageHeader,
            bufferLength: 1,
            LinuxIoUringEngine.SendMessageZeroCopyOperation,
            waitForNotification: true);
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

        var operation = useZeroCopy && buffer.Count >= ZeroCopyThreshold
            ? LinuxIoUringEngine.SendZeroCopyOperation
            : LinuxIoUringEngine.SendOperation;
        return Submit(
            socket,
            buffer,
            operation,
            bufferIsPinned,
            waitForNotification: operation == LinuxIoUringEngine.SendZeroCopyOperation);
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
    private static readonly Action<object?> ContinuationCompleted = static _ => { };
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

    public int BytesTransferred { get; private set; }

    public SocketError SocketError { get; private set; }

    public Exception? Error { get; private set; }

    [MemberNotNullWhen(true, nameof(Error))]
    public bool HasError => Error is not null;

    internal int FileDescriptor => _fileDescriptor;

    internal IntPtr BufferAddress
        => _bufferAddress;

    internal int BufferLength => _bufferLength;

    internal byte OperationCode { get; private set; }

    internal bool WaitForNotification => _waitForNotification;

    internal int PrimaryResult => _primaryResult;

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

        return SubmitPrepared(socket, _bufferAddress, _bufferLength, operationCode, waitForNotification);
    }

    protected ValueTask SubmitPrepared(
        Socket socket,
        IntPtr bufferAddress,
        int bufferLength,
        byte operationCode,
        bool waitForNotification)
    {
        try
        {
            var engine = LinuxIoUringEngine.Instance;
            _socket = socket;
            _fileDescriptor = checked((int)socket.Handle);
            _bufferAddress = bufferAddress;
            _bufferLength = bufferLength;
            OperationCode = operationCode;
            _waitForNotification = waitForNotification;
            _primaryResult = 0;
            BytesTransferred = 0;
            SocketError = SocketError.Success;
            Error = null;

            engine.Enqueue(this);
            return new ValueTask(this, 0);
        }
        catch
        {
            ReleaseSubmission();
            throw;
        }
    }

    internal void SetPrimaryResult(int result) => _primaryResult = result;

    internal void Complete(int result)
    {
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

        SignalCompletion();
    }

    internal void Complete(Exception error)
    {
        ReleaseSubmission();
        BytesTransferred = 0;
        SocketError = SocketError.SocketError;
        Error = error;
        SignalCompletion();
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
        Debug.Assert(_socket is null);
        Debug.Assert(_buffer is null);
        Debug.Assert(!_bufferPin.IsAllocated);
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

    private static SocketError MapSocketError(int errno) => errno switch
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

    private void SignalCompletion()
    {
        var continuation = _continuation;
        if (continuation is not null
            || (continuation = Interlocked.CompareExchange(ref _continuation, ContinuationCompleted, null)) is not null)
        {
            var state = _continuationState;
            _continuationState = null;
            _continuation = ContinuationCompleted;
            continuation(state);
        }
    }
}

internal sealed unsafe partial class LinuxIoUringEngine
{
    internal const byte SendOperation = 26;
    internal const byte ReceiveOperation = 27;
    internal const byte ReceiveMessageOperation = 10;
    internal const byte SendZeroCopyOperation = 47;
    internal const byte SendMessageZeroCopyOperation = 48;

    private const byte PollOperation = 6;
    private const uint PollIn = 1;
    private const uint CompletionHasMore = 1 << 1;
    private const uint CompletionIsNotification = 1 << 3;
    private const uint SetupCooperativeTaskRun = 1 << 8;
    private const uint SetupSingleIssuer = 1 << 12;
    private const uint SetupDeferTaskRun = 1 << 13;
    private const ulong WakeUserData = 1;
    private const int EventFdCloseOnExec = 0x80000;
    private const int EventFdNonBlocking = 0x800;

    private static readonly Lazy<LinuxIoUringEngine> LazyInstance = new(static () => new LinuxIoUringEngine());
    private readonly ConcurrentQueue<LinuxIoUringOperation> _pending = new();
    private readonly Dictionary<ulong, LinuxIoUringOperation> _inflight = [];
    private readonly ManualResetEventSlim _started = new(initialState: false);
    private IoUring _ring;
    private Exception? _fatalError;
    private int _enqueuers;
    private int _eventFd = -1;
    private int _engineThreadId;
    private bool _ringInitialized;
    private long _zeroCopyPrimaryCompletions;
    private long _zeroCopyNotificationCompletions;
    private long _zeroCopyFallbackCompletions;
    private ulong _nextUserData = WakeUserData;
    private bool _wakePollArmed;

    private LinuxIoUringEngine()
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("The io_uring transport requires Linux.");
        }

        if (!Environment.Is64BitProcess || !BitConverter.IsLittleEndian)
        {
            throw new PlatformNotSupportedException("The io_uring transport requires a little-endian 64-bit process.");
        }

        var thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "Orleans io_uring",
        };
        thread.Start();
        _started.Wait();
        if (Volatile.Read(ref _fatalError) is { } error)
        {
            throw new InvalidOperationException("Unable to start the Orleans io_uring engine.", error);
        }
    }

    public static LinuxIoUringEngine Instance => LazyInstance.Value;

    internal (long Primary, long Notifications, long Fallbacks) GetZeroCopyStatistics()
        => (
            Volatile.Read(ref _zeroCopyPrimaryCompletions),
            Volatile.Read(ref _zeroCopyNotificationCompletions),
            Volatile.Read(ref _zeroCopyFallbackCompletions));

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

        Interlocked.Decrement(ref _enqueuers);
    }

    private void Run()
    {
        try
        {
            Volatile.Write(ref _engineThreadId, Environment.CurrentManagedThreadId);
            var flags = SetupSingleIssuer | SetupDeferTaskRun | SetupCooperativeTaskRun;
            ThrowIfError(Native.QueueInit(4096, ref _ring, flags));
            _ringInitialized = true;
            _eventFd = Native.EventFd(0, EventFdCloseOnExec | EventFdNonBlocking);
            if (_eventFd < 0)
            {
                throw new InvalidOperationException($"Unable to create the io_uring eventfd. errno={Marshal.GetLastPInvokeError()}");
            }

            ArmWakePoll();
            _started.Set();

            while (true)
            {
                SubmitPending();
                SubmitAndWait();
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

            while (_pending.TryDequeue(out var operation))
            {
                failedOperations.Add(operation);
            }

            failedOperations.AddRange(_inflight.Values);
            _inflight.Clear();
            if (_ringInitialized)
            {
                _ringInitialized = false;
                Native.QueueExit(ref _ring);
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

            var userData = NextUserData();
            *sqe = default;
            sqe->Opcode = operation.OperationCode;
            sqe->FileDescriptor = operation.FileDescriptor;
            sqe->Address = (ulong)operation.BufferAddress;
            sqe->Length = checked((uint)operation.BufferLength);
            sqe->UserData = userData;
            _inflight.Add(userData, operation);

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
            Submit();
        }
    }

    private void DrainCompletions()
    {
        while (TryGetCompletion(out var completion))
        {
            var userData = completion.UserData;
            var result = completion.Result;
            var flags = completion.Flags;
            AdvanceCompletion();

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

            if (!_inflight.TryGetValue(userData, out var operation))
            {
                throw new InvalidOperationException($"io_uring completion {userData} has no owner.");
            }

            if (operation.WaitForNotification)
            {
                if ((flags & CompletionIsNotification) != 0)
                {
                    Interlocked.Increment(ref _zeroCopyNotificationCompletions);
                    _inflight.Remove(userData);
                    operation.Complete(operation.PrimaryResult);
                }
                else if ((flags & CompletionHasMore) == 0)
                {
                    if (result >= 0)
                    {
                        Interlocked.Increment(ref _zeroCopyFallbackCompletions);
                    }

                    _inflight.Remove(userData);
                    operation.Complete(result);
                }
                else
                {
                    Interlocked.Increment(ref _zeroCopyPrimaryCompletions);
                    operation.SetPrimaryResult(result);
                }
            }
            else
            {
                _inflight.Remove(userData);
                operation.Complete(result);
            }
        }
    }

    private IoUringSubmission* GetSubmission()
    {
        var result = Native.GetSubmission(ref _ring);
        if (result != null)
        {
            return result;
        }

        Submit();
        result = Native.GetSubmission(ref _ring);
        if (result == null)
        {
            throw new InvalidOperationException("The io_uring submission queue remained full after submission.");
        }

        return result;
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

        completion = _ring.Completion.Completions[head & *_ring.Completion.KernelRingMask];
        return true;
    }

    private void AdvanceCompletion()
    {
        var head = Volatile.Read(ref *_ring.Completion.KernelHead);
        Volatile.Write(ref *_ring.Completion.KernelHead, head + 1);
    }

    private ulong NextUserData()
    {
        do
        {
            _nextUserData++;
        }
        while (_nextUserData <= WakeUserData);

        return _nextUserData;
    }

    private static void ThrowIfError(int result)
    {
        if (result < 0)
        {
            throw new InvalidOperationException($"io_uring operation failed. errno={-result}");
        }
    }

    private void Submit()
    {
        int result;
        do
        {
            result = Native.Submit(ref _ring);
        }
        while (result == -4);

        ThrowIfError(result);
    }

    private void SubmitAndWait()
    {
        int result;
        do
        {
            result = Native.SubmitAndWait(ref _ring, 1);
        }
        while (result == -4);

        ThrowIfError(result);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoUringSubmissionQueue
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
    private struct IoUringCompletionQueue
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
    private struct IoUring
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

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct IoUringSubmission
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
    private readonly struct IoUringCompletion
    {
        internal readonly ulong UserData;
        internal readonly int Result;
        internal readonly uint Flags;
    }

    private static partial class Native
    {
        [LibraryImport("liburing.so.2", EntryPoint = "io_uring_queue_init")]
        internal static partial int QueueInit(uint entries, ref IoUring ring, uint flags);

        [LibraryImport("liburing.so.2", EntryPoint = "io_uring_get_sqe")]
        internal static partial IoUringSubmission* GetSubmission(ref IoUring ring);

        [LibraryImport("liburing.so.2", EntryPoint = "io_uring_submit")]
        internal static partial int Submit(ref IoUring ring);

        [LibraryImport("liburing.so.2", EntryPoint = "io_uring_submit_and_wait")]
        internal static partial int SubmitAndWait(ref IoUring ring, uint waitCount);

        [LibraryImport("liburing.so.2", EntryPoint = "io_uring_queue_exit")]
        internal static partial void QueueExit(ref IoUring ring);

        [LibraryImport("libc", EntryPoint = "eventfd", SetLastError = true)]
        internal static partial int EventFd(uint initialValue, int flags);

        [LibraryImport("libc", EntryPoint = "write", SetLastError = true)]
        internal static partial nint Write(int fileDescriptor, void* buffer, nuint count);

        [LibraryImport("libc", EntryPoint = "read", SetLastError = true)]
        internal static partial nint Read(int fileDescriptor, void* buffer, nuint count);

        [LibraryImport("libc", EntryPoint = "close")]
        internal static partial int Close(int fileDescriptor);
    }
}
