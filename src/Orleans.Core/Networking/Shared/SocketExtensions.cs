using System;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Orleans.Networking.Shared
{
    internal static class SocketExtensions
    {
        private const int SIO_LOOPBACK_FAST_PATH = -1744830448;
        private static readonly byte[] Enabled = BitConverter.GetBytes(1);

        /// <summary>
        /// Enables TCP Loopback Fast Path on a socket.
        /// See https://blogs.technet.microsoft.com/wincat/2012/12/05/fast-tcp-loopback-performance-and-low-latency-with-windows-server-2012-tcp-loopback-fast-path/
        /// for more information.
        /// </summary>
        /// <param name="socket">The socket for which FastPath should be enabled.</param>
        internal static void EnableFastPath(this Socket socket)
        {
            try { socket.NoDelay = true; } catch { }

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return;
            }

            try
            {
                // Win8/Server2012+ only
                var osVersion = Environment.OSVersion.Version;
                if (osVersion.Major > 6 || osVersion.Major == 6 && osVersion.Minor >= 2)
                {
                    socket.IOControl(SIO_LOOPBACK_FAST_PATH, Enabled, null);
                }
            }
            catch
            {
                // If the operating system version on this machine did
                // not support SIO_LOOPBACK_FAST_PATH (i.e. version
                // prior to Windows 8 / Windows Server 2012), handle the exception
            }
        }

        /// <summary>
        /// Enables TCP KeepAlive on a socket.
        /// </summary>
        /// <param name="socket">The socket.</param>
        internal static void EnableKeepAlive(this Socket socket, int timeSeconds = 90, int intervalSeconds = 30, int retryCount = 2)
        {
            try
            {
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, timeSeconds);
                socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, intervalSeconds);
                socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, retryCount);
            }
            catch
            {
            }
        }
    }
}
