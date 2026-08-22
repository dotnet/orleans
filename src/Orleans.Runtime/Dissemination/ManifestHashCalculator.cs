using System;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Orleans.Metadata;

namespace Orleans.Runtime.Dissemination;

internal static class ManifestHashCalculator
{
    public static ManifestHash ComputeHash(GrainManifest manifest)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendSection(hash, "grains");
        foreach (var grain in manifest.Grains.OrderBy(static entry => entry.Key.ToString(), StringComparer.Ordinal))
        {
            AppendString(hash, grain.Key.ToString() ?? string.Empty);
            AppendProperties(hash, grain.Value.Properties);
        }

        AppendSection(hash, "interfaces");
        foreach (var grainInterface in manifest.Interfaces.OrderBy(static entry => entry.Key.ToString(), StringComparer.Ordinal))
        {
            AppendString(hash, grainInterface.Key.ToString() ?? string.Empty);
            AppendProperties(hash, grainInterface.Value.Properties);
        }

        return new ManifestHash(Convert.ToHexString(hash.GetHashAndReset()));
    }

    private static void AppendProperties(IncrementalHash hash, System.Collections.Immutable.ImmutableDictionary<string, string> properties)
    {
        foreach (var property in properties.OrderBy(static entry => entry.Key, StringComparer.Ordinal))
        {
            AppendString(hash, property.Key);
            AppendString(hash, property.Value);
        }
    }

    private static void AppendSection(IncrementalHash hash, string section)
    {
        AppendString(hash, section);
        AppendString(hash, ":");
    }

    private static void AppendString(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
        var length = Encoding.UTF8.GetBytes(bytes.Length.ToString(CultureInfo.InvariantCulture));
        hash.AppendData(length);
        hash.AppendData(stackalloc byte[] { 0 });
        hash.AppendData(bytes);
        hash.AppendData(stackalloc byte[] { 0xff });
    }
}
