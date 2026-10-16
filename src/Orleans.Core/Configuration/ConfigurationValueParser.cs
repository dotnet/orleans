using System;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using Microsoft.Extensions.Configuration;

namespace Orleans.Configuration;

/// <summary>
/// Parses optional network address values from configuration, which <see cref="ConfigurationBinder"/> cannot convert.
/// </summary>
/// <remarks>
/// An absent or empty value leaves the caller's default in place, and an invalid value throws an
/// <see cref="InvalidOperationException"/> in the style of <see cref="ConfigurationBinder"/>.
/// </remarks>
internal static class ConfigurationValueParser
{
    /// <summary>
    /// Parses the <see cref="IPAddress"/> value at <paramref name="key"/>.
    /// </summary>
    /// <returns><see langword="true"/> when the key has a value; <see langword="false"/> when it is absent or empty.</returns>
    /// <exception cref="InvalidOperationException">The configured value is not a valid <see cref="IPAddress"/>.</exception>
    public static bool TryParseIPAddress(IConfiguration configuration, string key, [NotNullWhen(true)] out IPAddress? value)
    {
        var text = configuration[key];
        if (string.IsNullOrEmpty(text))
        {
            value = null;
            return false;
        }

        if (IPAddress.TryParse(text, out value))
        {
            return true;
        }

        throw CreateInvalidValueException(configuration, key, typeof(IPAddress));
    }

    /// <summary>
    /// Parses the <see cref="IPEndPoint"/> value at <paramref name="key"/>.
    /// </summary>
    /// <returns><see langword="true"/> when the key has a value; <see langword="false"/> when it is absent or empty.</returns>
    /// <exception cref="InvalidOperationException">The configured value is not a valid <see cref="IPEndPoint"/>.</exception>
    public static bool TryParseIPEndPoint(IConfiguration configuration, string key, [NotNullWhen(true)] out IPEndPoint? value)
    {
        var text = configuration[key];
        if (string.IsNullOrEmpty(text))
        {
            value = null;
            return false;
        }

        if (IPEndPoint.TryParse(text, out value))
        {
            return true;
        }

        throw CreateInvalidValueException(configuration, key, typeof(IPEndPoint));
    }

    private static InvalidOperationException CreateInvalidValueException(IConfiguration configuration, string key, Type type)
    {
        var path = configuration is IConfigurationSection section ? ConfigurationPath.Combine(section.Path, key) : key;
        return new InvalidOperationException($"Failed to convert configuration value at '{path}' to type '{type}'.");
    }
}