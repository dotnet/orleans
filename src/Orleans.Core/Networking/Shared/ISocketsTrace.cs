using System;
using Microsoft.Extensions.Logging;

namespace Orleans.Networking.Shared
{
    internal interface ISocketsTrace : ILogger
    {
        void ConnectionReadFin(string connectionId);

        void ConnectionWriteFin(string connectionId, string reason);

        void ConnectionError(string connectionId, Exception ex);

        void ConnectionReset(string connectionId);

        void ConnectionPause(string connectionId);

        void ConnectionResume(string connectionId);
    }
}
