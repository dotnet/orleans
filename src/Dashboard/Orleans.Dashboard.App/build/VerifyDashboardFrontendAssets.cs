using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

public sealed class VerifyDashboardFrontendAssets : Task
{
    [Required]
    public string Root { get; set; }

    public override bool Execute()
    {
        var inputs = Directory.EnumerateFiles(Root)
            .Where(path => !IsExcluded(Path.GetFileName(path)))
            .Concat(EnumerateTree(Path.Combine(Root, "src")))
            .Concat(EnumerateTree(Path.Combine(Root, "public")))
            .Concat(EnumerateTree(Path.Combine(Root, "scripts")))
            .Concat(EnumerateTree(Path.Combine(Root, "build")).Where(path => !IsManifest(path)));
        var sourcesMatch = Verify("assets.inputs.sha256", inputs, Root, normalizeText: true);
        var outputsMatch = Verify("assets.outputs.sha256", EnumerateTree(Path.Combine(Root, "dist")), Path.Combine(Root, "dist"), normalizeText: false);
        return sourcesMatch && outputsMatch;
    }

    private bool Verify(string manifest, IEnumerable<string> files, string root, bool normalizeText)
    {
        var manifestPath = Path.Combine(Root, "build", manifest);
        if (!File.Exists(manifestPath))
        {
            return Stale($"Missing {manifest}.");
        }

        var actual = files.Select(path =>
        {
            var bytes = File.ReadAllBytes(path);
            if (normalizeText && IsText(path))
            {
                bytes = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n"));
            }

            using (var algorithm = SHA256.Create())
            {
                var hash = BitConverter.ToString(algorithm.ComputeHash(bytes)).Replace("-", "");
                var relative = path.Substring(root.Length + 1).Replace('\\', '/');
                return hash + "  " + relative;
            }
        }).OrderBy(line => line, StringComparer.Ordinal).ToArray();
        var expected = File.ReadAllLines(manifestPath);
        if (actual.Length == 0 || !actual.SequenceEqual(expected, StringComparer.Ordinal))
        {
            var changed = actual.Except(expected, StringComparer.Ordinal)
                .Concat(expected.Except(actual, StringComparer.Ordinal))
                .Select(line => line.Length > 66 ? line.Substring(66) : line);
            return Stale($"{manifest} differs: {string.Join(", ", changed.Distinct(StringComparer.Ordinal))}.");
        }

        return true;
    }

    private bool Stale(string reason)
    {
        Log.LogError("", "ORLEANSDASHBOARD001", "", Root, 0, 0, 0, 0,
            "Dashboard frontend assets are stale. {0} In src/Dashboard/Orleans.Dashboard.App, use the pinned Node/npm toolchain and run 'npm run assets:generate', then commit dist/ and both build/assets.*.sha256 manifests.",
            reason);
        return false;
    }

    private static IEnumerable<string> EnumerateTree(string path) =>
        Directory.Exists(path) ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories) : Enumerable.Empty<string>();

    private static bool IsExcluded(string name) =>
        name == "README.md" || name == ".gitignore";

    private bool IsManifest(string path) =>
        path == Path.Combine(Root, "build", "assets.inputs.sha256") || path == Path.Combine(Root, "build", "assets.outputs.sha256");

    private static bool IsText(string path)
    {
        var name = Path.GetFileName(path);
        return name == ".npmrc" || name == ".node-version" || name.StartsWith(".env", StringComparison.Ordinal)
            || new[] { ".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs", ".mts", ".cts", ".cs", ".css", ".json", ".html", ".svg", ".txt", ".targets" }
                .Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    }
}
