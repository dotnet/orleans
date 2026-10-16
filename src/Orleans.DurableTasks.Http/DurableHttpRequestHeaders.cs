using System.Diagnostics.CodeAnalysis;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace Orleans.DurableTasks.Http;

/// <summary>Reads the <c>Idempotency-Key</c> and <c>Prefer</c> request headers.</summary>
internal static class DurableHttpRequestHeaders
{
    /// <summary>The longest key, in characters, which Stripe accepts.</summary>
    public const int MaxIdempotencyKeyLength = 255;

    /// <summary>
    /// Reads the request's idempotency key, which is a bare value or an RFC 9651 string, and returns
    /// <see langword="false"/> with a description when the header is present but invalid.
    /// </summary>
    /// <param name="headers">The request headers.</param>
    /// <param name="key">The key, or <see langword="null"/> when the request carries none.</param>
    /// <param name="error">A description of an invalid header.</param>
    public static bool TryGetIdempotencyKey(IHeaderDictionary headers, out string? key, [NotNullWhen(false)] out string? error)
    {
        key = null;
        error = null;
        var values = headers[DurableHttpHeaders.IdempotencyKey];
        if (values.Count == 0)
        {
            return true;
        }

        if (values.Count > 1)
        {
            error = "The request carries more than one Idempotency-Key header.";
            return false;
        }

        var value = values[0].AsSpan().Trim(' ');
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            if (!TryUnescape(value[1..^1], out key))
            {
                error = "The Idempotency-Key header is not a valid structured-field string.";
                return false;
            }
        }
        else
        {
            key = value.ToString();
        }

        if (!IsValidOperationId(key))
        {
            key = null;
            error = $"An Idempotency-Key has 1 to {MaxIdempotencyKeyLength} printable ASCII characters other than '/'.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Returns whether <paramref name="operationId"/> is a valid operation identifier: 1 to
    /// <see cref="MaxIdempotencyKeyLength"/> printable ASCII characters other than <c>/</c>.
    /// </summary>
    /// <remarks>
    /// An operation identifier is a URL path segment of its status monitor, and ASP.NET Core leaves an escaped <c>/</c> in a
    /// path segment escaped, so an identifier with one wouldn't address its operation.
    /// </remarks>
    public static bool IsValidOperationId(string operationId)
    {
        if (operationId.Length is 0 or > MaxIdempotencyKeyLength)
        {
            return false;
        }

        foreach (var character in operationId)
        {
            if (character is < ' ' or > '~' or '/')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Returns the time which the request's <c>Prefer: wait=N</c> preference asks the server to wait for a result, at most
    /// <paramref name="maxWait"/>, or <see cref="TimeSpan.Zero"/> when the request prefers no wait.
    /// </summary>
    /// <remarks>
    /// Preferences which the server doesn't recognize or can't parse are ignored, as RFC 7240 requires.
    /// <c>respond-async</c> asks for a response without waiting for a result, which is the default.
    /// </remarks>
    public static TimeSpan GetWait(IHeaderDictionary headers, TimeSpan maxWait)
    {
        foreach (var header in headers[DurableHttpHeaders.Prefer])
        {
            foreach (var preference in (header ?? string.Empty).Split(','))
            {
                var token = preference.AsSpan();
                if (token.IndexOf(';') is var parameters and >= 0)
                {
                    token = token[..parameters];
                }

                token = token.Trim();
                if (token.IndexOf('=') is var separator and > 0
                    && token[..separator].Trim().Equals("wait", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(token[(separator + 1)..].Trim().Trim('"'), System.Globalization.NumberStyles.None, null, out var seconds))
                {
                    var wait = TimeSpan.FromSeconds(seconds);
                    return wait < maxWait ? wait : maxWait;
                }
            }
        }

        return TimeSpan.Zero;
    }

    private static bool TryUnescape(ReadOnlySpan<char> value, [NotNullWhen(true)] out string? result)
    {
        var builder = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character == '\\')
            {
                if (++index == value.Length || value[index] is not ('"' or '\\'))
                {
                    result = null;
                    return false;
                }

                character = value[index];
            }
            else if (character == '"')
            {
                result = null;
                return false;
            }

            builder.Append(character);
        }

        result = builder.ToString();
        return true;
    }
}
