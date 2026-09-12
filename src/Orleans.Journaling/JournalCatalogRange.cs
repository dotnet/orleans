namespace Orleans.Journaling;

internal readonly struct JournalCatalogRange
{
    public JournalCatalogRange(ListOptions? options)
    {
        Prefix = options?.Prefix.Value;
        MinId = options?.MinId.Value;
        MaxId = options?.MaxId.Value;
        var prefixEnd = GetPrefixEnd(Prefix);
        LowerBound = Max(Prefix, MinId);
        UpperBound = Min(prefixEnd, MaxId);
        IsEmpty = LowerBound is not null && UpperBound is not null
            && string.CompareOrdinal(LowerBound, UpperBound) > 0;
        if (MinId is not null && prefixEnd is not null
            && string.CompareOrdinal(MinId, prefixEnd) >= 0)
        {
            IsEmpty = true;
        }

        var commonPrefix = GetCommonPrefix(MinId, MaxId);
        var listingPrefix = Prefix;
        if (!string.IsNullOrEmpty(commonPrefix))
        {
            if (listingPrefix is null || commonPrefix.StartsWith(listingPrefix, StringComparison.Ordinal))
            {
                listingPrefix = commonPrefix;
            }
            else if (!listingPrefix.StartsWith(commonPrefix, StringComparison.Ordinal))
            {
                IsEmpty = true;
            }
        }

        // Broaden only the native prefix to well-formed UTF-16; Contains retains the original ordinal range.
        for (var index = 0; listingPrefix is not null && index < listingPrefix.Length; index++)
        {
            var character = listingPrefix[index];
            if (!char.IsSurrogate(character))
            {
                continue;
            }

            if (char.IsHighSurrogate(character) && index + 1 < listingPrefix.Length
                && char.IsLowSurrogate(listingPrefix[index + 1]))
            {
                index++;
                continue;
            }

            listingPrefix = listingPrefix[..index];
            break;
        }

        ListingPrefix = string.IsNullOrEmpty(listingPrefix) ? null : listingPrefix;
    }

    public string? Prefix { get; }
    public string? MinId { get; }
    public string? MaxId { get; }
    public string? ListingPrefix { get; }
    public string? LowerBound { get; }
    public string? UpperBound { get; }
    public bool IsEmpty { get; }

    public bool Contains(string value)
        => !IsEmpty
            && (Prefix is null || value.StartsWith(Prefix, StringComparison.Ordinal))
            && (MinId is null || string.CompareOrdinal(value, MinId) >= 0)
            && (MaxId is null || string.CompareOrdinal(value, MaxId) <= 0);

    public string? GetUpperBoundForSuffix(string suffix)
    {
        if (MaxId is null || !System.Text.Ascii.IsValid(MaxId))
        {
            return null;
        }

        var result = MaxId + suffix;
        // Suffixes can move a shorter matching id beyond MaxId's storage key.
        for (var length = 1; length < MaxId.Length; length++)
        {
            var prefix = MaxId[..length];
            if (Contains(prefix))
            {
                var candidate = prefix + suffix;
                if (string.CompareOrdinal(candidate, result) > 0)
                {
                    result = candidate;
                }
            }
        }

        return result;
    }

    private static string? GetPrefixEnd(string? prefix)
    {
        if (prefix is null)
        {
            return null;
        }

        for (var index = prefix.Length - 1; index >= 0; index--)
        {
            if (prefix[index] != char.MaxValue)
            {
                return string.Concat(prefix.AsSpan(0, index), ((char)(prefix[index] + 1)).ToString());
            }
        }

        return null;
    }

    private static string? GetCommonPrefix(string? left, string? right)
    {
        if (left is null || right is null)
        {
            return null;
        }

        var length = 0;
        while (length < left.Length && length < right.Length && left[length] == right[length])
        {
            length++;
        }

        return left[..length];
    }

    private static string? Min(string? left, string? right)
        => left is null ? right : right is null || string.CompareOrdinal(left, right) <= 0 ? left : right;

    private static string? Max(string? left, string? right)
        => left is null ? right : right is null || string.CompareOrdinal(left, right) >= 0 ? left : right;
}
