#nullable enable
using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Connections;

namespace Orleans.Runtime.Messaging;

internal delegate MessageSerializer MessageSerializerFactory();

internal sealed class MessageHandlerShared(
    MessagingTrace messagingTrace,
    ConnectionTrace connectionTrace,
    MessageSerializerFactory serializerFactory,
    MessageFactory messageFactory,
    IMessageCenter messageCenter,
    MessagingInstruments messagingInstruments) : IDisposable
{
    private readonly MessageSerializerFactory _serializerFactory = serializerFactory;
    private readonly ConcurrentQueue<MessageSerializer> _serializerPool = new();
    private readonly ConcurrentQueue<MessageReadRequest> _receivePool = new();
    private readonly ConcurrentQueue<MessageWriteRequest> _sendPool = new();
    private readonly object _poolLock = new();
    private bool _disposing;
    private volatile bool _disposed;
    private int _activeSerializers;
    private int _activeSendWorkItems;

    public MessagingTrace MessagingTrace { get; } = messagingTrace;
    public ConnectionTrace ConnectionTrace { get; } = connectionTrace;
    public MessageFactory MessageFactory { get; } = messageFactory;
    public IMessageCenter MessageCenter { get; } = messageCenter;
    public MessagingInstruments MessagingInstruments { get; } = messagingInstruments;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal MessageSerializer GetMessageSerializer()
    {
        lock (_poolLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _activeSerializers++;
            if (_serializerPool.TryDequeue(out var result))
            {
                return result;
            }

            try
            {
                return _serializerFactory();
            }
            catch
            {
                _activeSerializers--;
                throw;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Return(MessageSerializer serializer)
    {
        lock (_poolLock)
        {
            _activeSerializers--;
            if (_disposing)
            {
                serializer.Dispose();
            }
            else
            {
                _serializerPool.Enqueue(serializer);
            }

            SignalIfQuiescent();
        }
    }

    internal bool TryAcquireSendWork()
    {
        lock (_poolLock)
        {
            if (_disposing)
            {
                return false;
            }

            _activeSendWorkItems++;
            return true;
        }
    }

    internal void ReleaseSendWork()
    {
        lock (_poolLock)
        {
            _activeSendWorkItems--;
            SignalIfQuiescent();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal MessageReadRequest GetReceiveMessageHandler()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_receivePool.TryDequeue(out var result))
        {
            return result;
        }

        return new(this);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Return(MessageReadRequest handler)
    {
        lock (_poolLock)
        {
            if (!_disposing)
            {
                _receivePool.Enqueue(handler);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal MessageWriteRequest GetSendMessageHandler()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_sendPool.TryDequeue(out var result))
        {
            return result;
        }

        return new(this);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal MessageWriteRequest GetSendMessageHandler(Connection connection)
    {
        var result = GetSendMessageHandler();
        result.Initialize(connection);
        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Return(MessageWriteRequest handler)
    {
        lock (_poolLock)
        {
            if (_disposing)
            {
                handler.Dispose();
            }
            else
            {
                _sendPool.Enqueue(handler);
            }
        }
    }

    public void Dispose()
    {
        lock (_poolLock)
        {
            if (_disposed)
            {
                return;
            }

            if (_disposing)
            {
                while (!_disposed)
                {
                    Monitor.Wait(_poolLock);
                }

                return;
            }

            _disposing = true;
            while (_serializerPool.TryDequeue(out var serializer))
            {
                serializer.Dispose();
            }

            while (_sendPool.TryDequeue(out var handler))
            {
                handler.Dispose();
            }

            _receivePool.Clear();
            while (_activeSerializers > 0 || _activeSendWorkItems > 0)
            {
                Monitor.Wait(_poolLock);
            }

            _disposed = true;
            Monitor.PulseAll(_poolLock);
        }
    }

    private void SignalIfQuiescent()
    {
        if (_activeSerializers == 0 && _activeSendWorkItems == 0)
        {
            Monitor.PulseAll(_poolLock);
        }
    }
}
