using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Orleans.Reminders.TestKit;

internal static class ReminderTestData
{
    public static Guid CreateGuid(int seed, string value)
    {
        var input = Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{seed}:{value}"));
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(input, hash);
        return new Guid(hash[..16]);
    }
}
