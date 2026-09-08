using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Orleans.Journaling;

internal sealed class JournalStorageCatalogToken
{
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);

    public string? Create(JournalId prefix, string? cursor)
        => cursor is null ? null : cursor + "." + Convert.ToBase64String(GetTag(prefix, cursor));

    public string? Parse(JournalId prefix, string? continuationToken)
    {
        if (continuationToken is null)
        {
            return null;
        }

        var separator = continuationToken.LastIndexOf('.');
        Span<byte> tag = stackalloc byte[32];
        if (separator <= 0
            || !Convert.TryFromBase64String(continuationToken[(separator + 1)..], tag, out var length)
            || length != tag.Length)
        {
            throw InvalidToken();
        }

        var cursor = continuationToken[..separator];
        if (!CryptographicOperations.FixedTimeEquals(tag, GetTag(prefix, cursor)))
        {
            throw InvalidToken();
        }

        return cursor;

        static ArgumentException InvalidToken()
            => new("The journal catalog continuation token is invalid for this prefix or provider instance.", nameof(continuationToken));
    }

    private byte[] GetTag(JournalId prefix, string cursor)
    {
        // Length-delimit the prefix and preserve the exact UTF-16 journal identity.
        // The instance-local key scopes tokens without retaining state for each traversal.
        var value = string.Concat(
            (prefix.Value?.Length ?? 0).ToString(CultureInfo.InvariantCulture), ":", prefix.Value, cursor);
        return HMACSHA256.HashData(_key, MemoryMarshal.AsBytes(value.AsSpan()));
    }
}
