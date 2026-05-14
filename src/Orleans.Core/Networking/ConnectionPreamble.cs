using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Threading.Tasks;
using Orleans.Connections.Transport;
using Orleans.Serialization;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Buffers.Adaptors;
using Orleans.Serialization.Session;

#nullable disable
namespace Orleans.Runtime.Messaging
{
    [GenerateSerializer, Immutable]
    internal sealed class ConnectionPreamble
    {
        [Id(0)]
        public NetworkProtocolVersion NetworkProtocolVersion { get; init; }

        [Id(1)]
        public GrainId NodeIdentity { get; init; }

        [Id(2)]
        public SiloAddress SiloAddress { get; init; }

        [Id(3)]
        public string ClusterId { get; init; }
    }

    internal sealed class ConnectionPreambleHelper
    {
        private const int MaxPreambleLength = 1024;
        private readonly Serializer<ConnectionPreamble> _preambleSerializer;
        private readonly SerializerSessionPool _serializerSessionPool;

        public ConnectionPreambleHelper(Serializer<ConnectionPreamble> preambleSerializer, SerializerSessionPool serializerSessionPool)
        {
            _preambleSerializer = preambleSerializer;
            _serializerSessionPool = serializerSessionPool;
        }

        internal async ValueTask Write(MessageTransport transport, ConnectionPreamble preamble)
        {
            using var writeRequest = PreambleWriteRequest.Create(preamble, _preambleSerializer, _serializerSessionPool);
            if (!transport.EnqueueWrite(writeRequest))
            {
                throw new ConnectionAbortedException();
            }

            await writeRequest.Completion;

            return;
        }

        internal async ValueTask<ConnectionPreamble> Read(MessageTransport transport)
        {
            using var readRequest = PreambleReadRequest.Create(_preambleSerializer);
            if (!transport.EnqueueRead(readRequest))
            {
                throw new ConnectionAbortedException();
            }

            var result = await readRequest.Completion;
            return result;
        }

        private sealed class PreambleWriteRequest : WriteRequest, IDisposable
        {
            private readonly TaskCompletionSource _completion = new();
            private readonly ArcBufferWriter _buffer;

            private PreambleWriteRequest(ArcBufferWriter buffer)
            {
                _buffer = buffer;
                Buffers = new (_buffer);
            }

            public static PreambleWriteRequest Create(ConnectionPreamble preamble, Serializer<ConnectionPreamble> preambleSerializer, SerializerSessionPool serializerSessionPool)
            {
                // Reserve space for framing
                var buffer = new ArcBufferWriter();
                var framingBytes = buffer.GetSpan(sizeof(int));
                buffer.AdvanceWriter(sizeof(int));

                // Serialize the preamble.
                using var session = serializerSessionPool.GetSession();
                var writer = Writer.Create(buffer, session);
                preambleSerializer.Serialize(preamble, ref writer);

                // Write framing
                var length = writer.Position;
                BinaryPrimitives.WriteInt32LittleEndian(framingBytes, length);

                if (length > MaxPreambleLength)
                {
                    throw new InvalidOperationException($"Created preamble of length {length}, which is greater than maximum allowed size of {MaxPreambleLength}.");
                }

                return new(buffer);
            }

            public override void SetResult() => _completion.SetResult();
            public override void SetException(Exception error) => _completion.SetException(error);

            public void Dispose() => _buffer.Dispose();

            public Task Completion => _completion.Task;
        }

        private sealed class PreambleReadRequest : ReadRequest, IDisposable
        {
            private readonly Serializer<ConnectionPreamble> _preambleSerializer;
            private readonly TaskCompletionSource<ConnectionPreamble> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private int _preambleLength = -1;

            private PreambleReadRequest(Serializer<ConnectionPreamble> preambleSerializer)
            {
                _preambleSerializer = preambleSerializer;
            }

            public Task<ConnectionPreamble> Completion => _completion.Task;

            public static PreambleReadRequest Create(Serializer<ConnectionPreamble> preambleSerializer) => new (preambleSerializer);

            public void Dispose() { }
            public override void OnError(Exception error) => _completion.SetException(error);
            public override void OnCanceled() => _completion.SetException(new OperationCanceledException("Read operation canceled"));
            public override bool OnRead(ArcBufferReader buffer)
            {
                if (buffer.Length < sizeof(int))
                {
                    return false;
                }

                if (_preambleLength < 0)
                {
                    Span<byte> preambleBytes = stackalloc byte[sizeof(int)];
                    var preambleBuffer = buffer.Peek(in preambleBytes);
                    _preambleLength = BinaryPrimitives.ReadInt32LittleEndian(preambleBuffer);

                    if (_preambleLength > MaxPreambleLength)
                    {
                        throw new InvalidOperationException($"Read preamble length of {_preambleLength}, which is greater than maximum allowed size of {MaxPreambleLength}.");
                    }

                    if (_preambleLength <= 0)
                    {
                        throw new InvalidOperationException($"Read preamble length of {_preambleLength}, which is less than or equal to zero.");
                    }
                }

                if (buffer.Length >= _preambleLength + sizeof(int))
                {
                    buffer.Skip(sizeof(int));
                    using var preambleBuffer = buffer.ConsumeSlice(_preambleLength);
                    var preamble = _preambleSerializer.Deserialize(preambleBuffer);
                    _completion.SetResult(preamble);

                    return true;
                }

                return false;
            }
        }
    }
}
