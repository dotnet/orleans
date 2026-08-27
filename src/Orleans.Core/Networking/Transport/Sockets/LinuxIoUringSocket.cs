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

        var useZeroCopyOperation = useZeroCopy && totalLength >= ZeroCopyThreshold;

        if (buffers.Count == 1)
        {
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

    protected LinuxIoUringOperation()
    {
        _engine = LinuxIoUringEngine.GetNext();
        (_slotToken, _generation) = _engine.Register(this);
    }

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

    internal uint SlotToken => _slotToken;

    internal ulong UserData => Volatile.Read(ref _userData);

    internal ulong Generation => _generation;

    internal bool IsPending => Volatile.Read(ref _state) == StatePending;

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

    protected ValueTask SubmitPrepared(
        Socket socket,
        IntPtr bufferAddress,
        int bufferLength,
        byte operationCode,
        bool waitForNotification)
    {
        try
        {
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

    internal void SetPrimaryResult(int result) => _primaryResult = result;

    internal void Complete(int result)
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
        SignalCompletion();
    }

    internal void Complete(Exception error)
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
        if (Interlocked.CompareExchange(ref _state, StateDisposed, StateIdle) != StateIdle)
        {
            throw new InvalidOperationException("An active io_uring operation cannot be disposed.");
        }

        Debug.Assert(_socket is null);
        Debug.Assert(_buffer is null);
        Debug.Assert(!_bufferPin.IsAllocated);
        _engine.Unregister(this);
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
    internal const byte SendMessageOperation = 9;
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
    private const int InitialOperationCapacity = 256;
    private const int TargetProcessorsPerEngine = 4;
    private const int MaximumEngineCount = 4;
    internal const int UserDataSlotBits = 20;
    internal const ulong UserDataSlotMask = (1UL << UserDataSlotBits) - 1;
    internal const ulong UserDataGenerationMask = ulong.MaxValue >> UserDataSlotBits;

    private static readonly Lazy<LinuxIoUringEngine>[] Engines = CreateEngines();
    private static int _nextEngine = -1;
    private readonly ConcurrentQueue<LinuxIoUringOperation> _pending = new();
    private readonly object _operationsLock = new();
    private readonly Stack<int> _freeOperationSlots = [];
    private readonly ManualResetEventSlim _started = new(initialState: false);
    private LinuxIoUringOperation?[] _operations = new LinuxIoUringOperation[InitialOperationCapacity];
    private ulong[] _operationGenerations = new ulong[InitialOperationCapacity];
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
    private bool _wakePollArmed;

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

    internal static (long Primary, long Notifications, long Fallbacks) GetZeroCopyStatistics()
    {
        long primary = 0;
        long notifications = 0;
        long fallbacks = 0;
        foreach (var engine in Engines)
        {
            if (engine.IsValueCreated)
            {
                primary += Volatile.Read(ref engine.Value._zeroCopyPrimaryCompletions);
                notifications += Volatile.Read(ref engine.Value._zeroCopyNotificationCompletions);
                fallbacks += Volatile.Read(ref engine.Value._zeroCopyFallbackCompletions);
            }
        }

        return (primary, notifications, fallbacks);
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

            while (_pending.TryDequeue(out _))
            {
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

            *sqe = default;
            sqe->Opcode = operation.OperationCode;
            sqe->FileDescriptor = operation.FileDescriptor;
            sqe->Address = (ulong)operation.BufferAddress;
            sqe->Length = checked((uint)operation.BufferLength);
            sqe->UserData = operation.UserData;

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

            if (!TryGetOperation(userData, out var operation))
            {
                throw new InvalidOperationException($"io_uring completion {userData} has no owner.");
            }

            if (operation.WaitForNotification)
            {
                if ((flags & CompletionIsNotification) != 0)
                {
                    Interlocked.Increment(ref _zeroCopyNotificationCompletions);
                    operation.Complete(operation.PrimaryResult);
                }
                else if ((flags & CompletionHasMore) == 0)
                {
                    if (result >= 0)
                    {
                        Interlocked.Increment(ref _zeroCopyFallbackCompletions);
                    }

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
                operation.Complete(result);
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
