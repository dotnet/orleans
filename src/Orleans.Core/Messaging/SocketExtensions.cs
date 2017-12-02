using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace Orleans.Messaging
{
    internal static class SocketExtensions
    {
        /// <summary>
        /// Enables TCP Loopback Fast Path on a socket.
        /// See https://blogs.technet.microsoft.com/wincat/2012/12/05/fast-tcp-loopback-performance-and-low-latency-with-windows-server-2012-tcp-loopback-fast-path/
        /// for more information.
        /// </summary>
        /// <param name="socket">The socket for which FastPath should be enabled.</param>
        internal static void EnableFastpath(this Socket socket)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return;

            const int SIO_LOOPBACK_FAST_PATH = -1744830448;
            var optionInValue = BitConverter.GetBytes(1);
            try
            {
                socket.IOControl(SIO_LOOPBACK_FAST_PATH, optionInValue, null);
            }
            catch
            {
                // If the operating system version on this machine did
                // not support SIO_LOOPBACK_FAST_PATH (i.e. version
                // prior to Windows 8 / Windows Server 2012), handle the exception
            }
            
        }


    }
}
