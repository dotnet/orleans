using System.Buffers;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Net;
using System.Threading;

namespace Orleans.Networking.Shared
{
    internal abstract partial class TransportConnection : ConnectionContext
    {
        private IDictionary<object, object> _items;
        private string _connectionId;

        public TransportConnection()
        {
            FastReset();
        }

        public override EndPoint LocalEndPoint { get; set; }
        public override EndPoint RemoteEndPoint { get; set; }

        public override string ConnectionId
        {
            get
            {
                if (_connectionId == null)
                {
                    _connectionId = CorrelationIdGenerator.GetNextId();
                }

                return _connectionId;
            }
            set
            {
                _connectionId = value;
            }
        }

        public override IFeatureCollection Features => this;

        public virtual MemoryPool<byte> MemoryPool { get; }

        public override IDuplexPipe Transport { get; set; }

        public IDuplexPipe Application { get; set; }

        public override IDictionary<object, object> Items
        {
            get
            {
                // Lazily allocate connection metadata
                return _items ?? (_items = new ConnectionItems());
            }
            set
            {
                _items = value;
            }
        }

        public override CancellationToken ConnectionClosed { get; set; }

        // DO NOT remove this override to ConnectionContext.Abort. Doing so would cause
        // any TransportConnection that does not override Abort or calls base.Abort
        // to stack overflow when IConnectionLifetimeFeature.Abort() is called.
        // That said, all derived types should override this method should override
        // this implementation of Abort because canceling pending output reads is not
        // sufficient to abort the connection if there is backpressure.
        public override void Abort(ConnectionAbortedException abortReason)
        {
            Application.Input.CancelPendingRead();
        }
    }
}
