using System.Security.Cryptography;

namespace Orleans.Journaling;

internal static class CassandraJournalGenerationId
{
    private const ulong MaxTimestamp = 0x0000_FFFF_FFFF_FFFF;

    public static Guid Create() => Create(DateTimeOffset.UtcNow);

    public static Guid Create(DateTimeOffset timestamp)
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        WriteTimestamp(bytes, timestamp);
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x70);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes, bigEndian: true);
    }

    public static Guid CreateCleanupCutoff()
    {
        Span<byte> bytes = stackalloc byte[16];
        bytes.Clear();
        WriteTimestamp(bytes, DateTimeOffset.UtcNow.AddDays(-1));
        // Use the minimum random bits so every UUIDv7 from the cutoff millisecond sorts at or above it.
        bytes[6] = 0x70;
        bytes[8] = 0x80;
        return new Guid(bytes, bigEndian: true);
    }

    private static void WriteTimestamp(Span<byte> bytes, DateTimeOffset timestamp)
    {
        var milliseconds = timestamp.ToUnixTimeMilliseconds();
        if (milliseconds < 0 || (ulong)milliseconds > MaxTimestamp)
        {
            throw new ArgumentOutOfRangeException(nameof(timestamp), "The timestamp is outside the UUIDv7 range.");
        }

        var value = (ulong)milliseconds;
        bytes[0] = (byte)(value >> 40);
        bytes[1] = (byte)(value >> 32);
        bytes[2] = (byte)(value >> 24);
        bytes[3] = (byte)(value >> 16);
        bytes[4] = (byte)(value >> 8);
        bytes[5] = (byte)value;
    }
}
