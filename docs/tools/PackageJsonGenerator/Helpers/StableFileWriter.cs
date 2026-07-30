// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;

namespace PackageJsonGenerator.Helpers;

internal static class StableFileWriter
{
    private static readonly UTF8Encoding s_utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);

    public static bool WriteIfChanged(string path, string content)
    {
        var normalizedContent = NormalizeLineEndings(content);

        if (File.Exists(path))
        {
            var existingContent = File.ReadAllText(path);
            if (string.Equals(existingContent, normalizedContent, StringComparison.Ordinal))
            {
                return false;
            }
        }

        File.WriteAllText(path, normalizedContent, s_utf8WithoutBom);
        return true;
    }

    private static string NormalizeLineEndings(string content)
    {
        return content.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal);
    }
}