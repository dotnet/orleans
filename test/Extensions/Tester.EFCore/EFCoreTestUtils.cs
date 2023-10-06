using System.Net.Sockets;

namespace Tester.EFCore;

public static class EFCoreTestUtils
{
    public static void CheckSqlServer() => IsPortOpen("localhost", 1433);
    public static void CheckMySql() => IsPortOpen("localhost", 3306);

    private static bool IsPortOpen(string host, int port)
    {
        using var client = new TcpClient();
        try
        {
            var result = client.BeginConnect(host, port, null, null);
            var success = result.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(300));
            if (!success)
            {
                throw new TimeoutException("Connection timed out.");
            }

            client.EndConnect(result);
            return true;
        }
        catch
        {
            throw new SkipException();
        }
    }
}