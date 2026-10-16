using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using Microsoft.Extensions.Configuration;

namespace Orleans.Configuration;

/// <summary>
/// Parses optional typed values from configuration without reflection.
/// </summary>
/// <remarks>
/// Orleans.Core and Orleans.Runtime use this instead of <see cref="ConfigurationBinder.GetValue{T}(IConfiguration, string)"/>,
/// which requires unreferenced code unless the configuration binding source generator is enabled. These assemblies
/// cannot enable the generator because they bind options types with members the generator does not support.
/// An absent or empty value leaves the caller's default in place, and an invalid value throws an
/// <see cref="InvalidOperationException"/> in the style of <see cref="ConfigurationBinder"/>.
/// </remarks>
internal static class ConfigurationValueParser
{
    /// <summary>
    /// Parses the value at <paramref name="key"/> using <see cref="CultureInfo.InvariantCulture"/>.
    /// </summary>
    /// <returns><see langword="true"/> when the key has a value; <see langword="false"/> when it is absent or empty.</returns>
    /// <exception cref="InvalidOperationException">The configured value is not a valid <typeparamref name="T"/>.</exception>
    public static bool TryParseValue<T>(IConfiguration configuration, string key, [MaybeNullWhen(false)] out T value) where T : IParsable<T>
    {
        var text = configuration[key];
        if (string.IsNullOrEmpty(text))
        {
            value = default;
            return false;
        }

        if (T.TryParse(text, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }

        throw CreateInvalidValueException(configuration, key, typeof(T));
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