#nullable enable

using System;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Connections.Transport;
using Orleans.Connections.Transport.Sockets;

namespace Orleans.TestingHost.UnixSocketTransport;

public class UnixDomainSocketMessageTransportListenerOptions
{
    public string Path { get; set; } = CreateDefaultPath();
    public bool Enabled { get; set; } = true;
    private static string CreateDefaultPath() => System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"silo_{Guid.NewGuid():N}");
}

internal class UnixDomainSocketMessageTransportListener : MessageTransportListener
{
    private readonly CancellationTokenSource _closingCts = new();
    private Socket? _listenSocket;
    private string? _boundPath;
    private int _shutdownStarted;
    private int _disposeStarted;
    private readonly IOptionsMonitor<UnixDomainSocketMessageTransportListenerOptions> _listenerOptions;

    internal UnixDomainSocketMessageTransportListener(
        string endpointName,
        IOptionsMonitor<UnixDomainSocketMessageTransportListenerOptions> listenerOptions,
        ILoggerFactory loggerFactory)
    {
        ListenerName = endpointName;
        _listenerOptions = listenerOptions;
        Logger = loggerFactory.CreateLogger("Orleans.Connections.Transport.Sockets");
    }

    protected ILogger Logger { get; }

    /// <inheritdoc/>
    public override FeatureCollection Features { get; } = new FeatureCollection();

    /// <inheritdoc/>
    public override bool IsValid => Socket.OSSupportsUnixDomainSockets && _listenerOptions.Get(ListenerName).Enabled;

    /// <inheritdoc/>
    public override string ListenerName { get; }

    protected virtual Socket CreateListenSocket()
    {
        var listenSocket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

        return listenSocket;
    }

    public override ValueTask BindAsync(CancellationToken cancellationToken = default)
    {
        if (_listenSocket != null)
        {
            throw new InvalidOperationException("Transport already bound");
        }

        var options = _listenerOptions.Get(ListenerName);
        var path = options.Path;
        var pathLock = AcquirePathLock(path, cancellationToken);
        try
        {
            DeleteStaleSocketFile(path);
            var listenSocket = CreateListenSocket();
            var bound = false;
            try
            {
                listenSocket.Bind(new UnixDomainSocketEndPoint(path));
                bound = true;
                listenSocket.Listen(512);
            }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                listenSocket.Dispose();
                throw new AddressInUseException(e.Message, e);
            }
            catch
            {
                listenSocket.Dispose();
                if (bound && !string.IsNullOrEmpty(path) && path[0] != '\0')
                {
                    File.Delete(path);
                }

                throw;
            }

            _boundPath = path;
            _listenSocket = listenSocket;
            return default;
        }
        finally
        {
            pathLock?.Dispose();
        }
    }

    public override async ValueTask<MessageTransport?> AcceptAsync(CancellationToken cancellationToken = default)
    {
        var listenSocket = _listenSocket ?? throw new InvalidOperationException("Transport is not bound");
        using var ct = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closingCts.Token);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var acceptSocket = await listenSocket.AcceptAsync(ct.Token).ConfigureAwait(false);
                var connection = new SocketMessageTransport(acceptSocket, Logger);
                connection.Start();

                return connection;
            }
            catch (OperationCanceledException)
            {
                // Graceful termination.
                return null;
            }
            catch (ObjectDisposedException)
            {
                // A call was made to UnbindAsync/DisposeAsync just return null which signals we're done
                return null;
            }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.OperationAborted)
            {
                // A call was made to UnbindAsync/DisposeAsync just return null which signals we're done
                return null;
            }
            catch (SocketException)
            {
                // The connection got reset while it was in the backlog, so we try again.
                SocketsLog.ConnectionReset(Logger, connection: "(null)");
            }
        }

        return null;
    }

    private void DisposeCore()
    {
        if (Interlocked.Exchange(ref _shutdownStarted, 1) != 0)
        {
            return;
        }

        _closingCts.Cancel();
        var listenSocket = Interlocked.Exchange(ref _listenSocket, null);
        var path = Interlocked.Exchange(ref _boundPath, null);
        if (string.IsNullOrEmpty(path) || path[0] == '\0')
        {
            listenSocket?.Dispose();
            return;
        }

        IDisposable? pathLock = null;
        try
        {
            pathLock = AcquirePathLock(path, CancellationToken.None);
            listenSocket?.Dispose();
            listenSocket = null;
            File.Delete(path);
        }
        finally
        {
            listenSocket?.Dispose();
            pathLock?.Dispose();
        }
    }

    private static void DeleteStaleSocketFile(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            socket.Connect(new UnixDomainSocketEndPoint(path));
        }
        catch (SocketException exception) when (
            exception.SocketErrorCode == SocketError.ConnectionRefused
            && IsUnixSocket(path))
        {
            File.Delete(path);
            return;
        }

        throw new AddressInUseException(
            $"A filesystem entry already exists at Unix domain socket path '{path}'.",
            new SocketException((int)SocketError.AddressAlreadyInUse));
    }

    private static bool IsUnixSocket(string path)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return false;
        }

        try
        {
            var pathBuffer = Marshal.StringToCoTaskMemUTF8(path);
            try
            {
                if (OperatingSystem.IsLinux())
                {
                    return NativeMethods.Statx(
                        NativeMethods.CurrentWorkingDirectory,
                        pathBuffer,
                        NativeMethods.NoFollow,
                        NativeMethods.Type,
                        out var stat) == 0
                        && (stat.Mode & NativeMethods.FileTypeMask) == NativeMethods.Socket;
                }

                return NativeMethods.LStat(pathBuffer, out var macStat) == 0
                    && (macStat.Mode & NativeMethods.FileTypeMask) == NativeMethods.Socket;
            }
            finally
            {
                Marshal.FreeCoTaskMem(pathBuffer);
            }
        }

        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static IDisposable? AcquirePathLock(string path, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(path) || path[0] == '\0')
        {
            return null;
        }

        return OperatingSystem.IsWindows()
            ? AcquireWindowsPathLock(cancellationToken)
            : AcquireUnixPathLock(cancellationToken);
    }

    private static IDisposable AcquireWindowsPathLock(CancellationToken cancellationToken)
    {
        var mutex = new Mutex(initiallyOwned: false, @"Global\orleans-unix-socket-path-lock");
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (mutex.WaitOne(TimeSpan.FromMilliseconds(100)))
                    {
                        return new WindowsPathLock(mutex);
                    }
                }
                catch (AbandonedMutexException)
                {
                    return new WindowsPathLock(mutex);
                }
            }
        }
        catch
        {
            mutex.Dispose();
            throw;
        }
    }

    private static IDisposable AcquireUnixPathLock(CancellationToken cancellationToken)
    {
        const string lockPath = "/tmp/.orleans-unix-socket-path.lock";
        var stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
        try
        {
            var fileDescriptor = checked((int)stream.SafeFileHandle.DangerousGetHandle());
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (NativeMethods.Flock(fileDescriptor, NativeMethods.LockExclusive | NativeMethods.LockNonBlocking) == 0)
                {
                    return new UnixPathLock(stream, fileDescriptor);
                }

                var error = Marshal.GetLastPInvokeError();
                if (error is not (NativeMethods.LinuxWouldBlock or NativeMethods.MacWouldBlock))
                {
                    throw new IOException($"Unable to lock Unix domain socket lifecycle file '{lockPath}'. errno={error}");
                }

                Thread.Sleep(100);
            }
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public override ValueTask UnbindAsync(CancellationToken cancellationToken)
    {
        DisposeCore();
        return default;
    }

    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            return;
        }

        DisposeCore();
        GC.SuppressFinalize(this);
        try
        {
            await base.DisposeAsync();
        }
        finally
        {
            _closingCts.Dispose();
        }
    }

    private static class NativeMethods
    {
        internal const int CurrentWorkingDirectory = -100;
        internal const int NoFollow = 0x100;
        internal const uint Type = 0x1;
        internal const ushort FileTypeMask = 0xF000;
        internal const ushort Socket = 0xC000;
        internal const int LockExclusive = 2;
        internal const int LockNonBlocking = 4;
        internal const int LockUnlock = 8;
        internal const int LinuxWouldBlock = 11;
        internal const int MacWouldBlock = 35;

        [DllImport("libc", EntryPoint = "statx", ExactSpelling = true, SetLastError = true)]
        internal static extern int Statx(
            int directoryFileDescriptor,
            IntPtr path,
            int flags,
            uint mask,
            out StatxBuffer buffer);

        [DllImport("libc", EntryPoint = "lstat", ExactSpelling = true, SetLastError = true)]
        internal static extern int LStat(IntPtr path, out MacStatBuffer buffer);

        [DllImport("libc", EntryPoint = "flock", ExactSpelling = true, SetLastError = true)]
        internal static extern int Flock(int fileDescriptor, int operation);
    }

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct StatxBuffer
    {
        [FieldOffset(28)]
        internal ushort Mode;
    }

    [StructLayout(LayoutKind.Explicit, Size = 144)]
    private struct MacStatBuffer
    {
        [FieldOffset(4)]
        internal ushort Mode;
    }

    private sealed class WindowsPathLock(Mutex mutex) : IDisposable
    {
        private Mutex? _mutex = mutex;

        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _mutex, null);
            if (current is null)
            {
                return;
            }

            current.ReleaseMutex();
            current.Dispose();
        }
    }

    private sealed class UnixPathLock(FileStream stream, int fileDescriptor) : IDisposable
    {
        private FileStream? _stream = stream;

        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _stream, null);
            if (current is null)
            {
                return;
            }

            var result = NativeMethods.Flock(fileDescriptor, NativeMethods.LockUnlock);
            var error = Marshal.GetLastPInvokeError();
            current.Dispose();
            if (result != 0)
            {
                throw new IOException($"Unable to unlock the Unix domain socket lifecycle file. errno={error}");
            }
        }
    }
}
