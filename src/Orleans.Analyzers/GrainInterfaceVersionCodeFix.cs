#nullable enable

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Immutable;
using System.Composition;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Analyzers;

/// <summary>
/// A code fix provider that adds grain interfaces to GrainInterfaces.txt or updates their version.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(GrainInterfaceVersionCodeFix)), Shared]
public class GrainInterfaceVersionCodeFix : CodeFixProvider
{
    private const string NewLine = "\r\n";
    public sealed override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create(
        GrainInterfaceVersionAnalyzer.RuleId0016,  // Interface not declared
        GrainInterfaceVersionAnalyzer.RuleId0017,  // Version mismatch
        GrainInterfaceVersionAnalyzer.RuleId0018,  // Member not declared
        GrainInterfaceVersionAnalyzer.RuleId0019); // Removed interface not retired

    // Note: We don't use BatchFixer because each fix may need to coordinate updates to the same file
    public sealed override FixAllProvider? GetFixAllProvider() => null;

    public sealed override Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        foreach (var diagnostic in context.Diagnostics)
        {
            switch (diagnostic.Id)
            {
                case GrainInterfaceVersionAnalyzer.RuleId0016:
                    RegisterAddInterfaceCodeFix(context, diagnostic);
                    break;
                case GrainInterfaceVersionAnalyzer.RuleId0017:
                    RegisterUpdateVersionCodeFix(context, diagnostic);
                    break;
                case GrainInterfaceVersionAnalyzer.RuleId0018:
                    RegisterAddMemberCodeFix(context, diagnostic);
                    break;
                case GrainInterfaceVersionAnalyzer.RuleId0019:
                    RegisterRetireInterfaceCodeFix(context, diagnostic);
                    break;
            }
        }

        return Task.CompletedTask;
    }

    private static void RegisterAddInterfaceCodeFix(CodeFixContext context, Diagnostic diagnostic)
    {
        if (!diagnostic.Properties.TryGetValue(GrainInterfaceVersionAnalyzer.InterfaceNamePropertyKey, out var interfaceName) ||
            string.IsNullOrEmpty(interfaceName))
        {
            return;
        }

        context.RegisterCodeFix(
            CodeAction.Create(
                title: Resources.AddToGrainInterfacesFileTitle,
                createChangedSolution: ct => AddInterfaceToFileAsync(context.Document, interfaceName!, ct),
                equivalenceKey: GrainInterfaceVersionAnalyzer.RuleId0016),
            diagnostic);
    }

    private static void RegisterUpdateVersionCodeFix(CodeFixContext context, Diagnostic diagnostic)
    {
        if (!diagnostic.Properties.TryGetValue(GrainInterfaceVersionAnalyzer.InterfaceNamePropertyKey, out var interfaceName) ||
            string.IsNullOrEmpty(interfaceName))
        {
            return;
        }

        if (!diagnostic.Properties.TryGetValue(GrainInterfaceVersionAnalyzer.ActualVersionPropertyKey, out var actualVersion) ||
            string.IsNullOrEmpty(actualVersion))
        {
            return;
        }

        context.RegisterCodeFix(
            CodeAction.Create(
                title: Resources.UpdateGrainInterfaceVersionTitle,
                createChangedSolution: ct => UpdateVersionInFileAsync(context.Document, interfaceName!, actualVersion!, ct),
                equivalenceKey: GrainInterfaceVersionAnalyzer.RuleId0017),
            diagnostic);
    }

    private static void RegisterAddMemberCodeFix(CodeFixContext context, Diagnostic diagnostic)
    {
        if (!diagnostic.Properties.TryGetValue(GrainInterfaceVersionAnalyzer.InterfaceNamePropertyKey, out var interfaceName) ||
            string.IsNullOrEmpty(interfaceName))
        {
            return;
        }

        if (!diagnostic.Properties.TryGetValue(GrainInterfaceVersionAnalyzer.MemberNamePropertyKey, out var memberSignature) ||
            string.IsNullOrEmpty(memberSignature))
        {
            return;
        }

        context.RegisterCodeFix(
            CodeAction.Create(
                title: Resources.AddToGrainInterfacesFileTitle,
                createChangedSolution: ct => AddMemberToFileAsync(context.Document, interfaceName!, memberSignature!, ct),
                equivalenceKey: GrainInterfaceVersionAnalyzer.RuleId0018),
            diagnostic);
    }

    private static void RegisterRetireInterfaceCodeFix(CodeFixContext context, Diagnostic diagnostic)
    {
        if (!diagnostic.Properties.TryGetValue(GrainInterfaceVersionAnalyzer.InterfaceNamePropertyKey, out var interfaceName) ||
            string.IsNullOrEmpty(interfaceName))
        {
            return;
        }

        context.RegisterCodeFix(
            CodeAction.Create(
                title: Resources.RetireGrainInterfaceTitle,
                createChangedSolution: ct => RetireInterfaceInFileAsync(context.Document, interfaceName!, ct),
                equivalenceKey: GrainInterfaceVersionAnalyzer.RuleId0019),
            diagnostic);
    }

    private static async Task<Solution> AddInterfaceToFileAsync(
        Document document,
        string interfaceName,
        CancellationToken cancellationToken)
    {
        var project = document.Project;
        var solution = project.Solution;

        // Get version and alias from the source code
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);

        if (semanticModel is null || root is null)
        {
            return solution;
        }

        var interfaceDecl = root.DescendantNodes()
            .OfType<InterfaceDeclarationSyntax>()
            .FirstOrDefault(i =>
            {
                var symbol = semanticModel.GetDeclaredSymbol(i, cancellationToken);
                if (symbol is null) return false;
                var fullName = symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", "");
                return string.Equals(fullName, interfaceName, StringComparison.Ordinal);
            });

        if (interfaceDecl is null)
        {
            return solution;
        }

        var symbol = semanticModel.GetDeclaredSymbol(interfaceDecl, cancellationToken) as INamedTypeSymbol;
        if (symbol is null)
        {
            return solution;
        }

        var version = GetVersionFromAttributes(symbol);
        var alias = GetAliasFromAttributes(symbol);

        // Build the interface declaration line
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(alias))
        {
            sb.Append($"[Alias(\"{alias}\")] ");
        }
        sb.Append(interfaceName);
        sb.Append($" [Version({version})]");
        var interfaceLine = sb.ToString();

        // Build member lines
        var memberLines = new StringBuilder();
        foreach (var member in symbol.GetMembers().OfType<IMethodSymbol>())
        {
            if (member.MethodKind != MethodKind.Ordinary)
            {
                continue;
            }

            var memberAlias = GetAliasFromAttributes(member);
            var memberSignature = GetMethodSignature(member);

            if (!string.IsNullOrEmpty(memberAlias))
            {
                memberLines.Append($"[Alias(\"{memberAlias}\")] ");
            }
            memberLines.AppendLine(memberSignature);
        }

        // Find or create the GrainInterfaces.txt file
        var grainInterfacesFile = project.AdditionalDocuments
            .FirstOrDefault(d => Path.GetFileName(d.FilePath ?? d.Name).Equals(Constants.GrainInterfacesFileName, StringComparison.OrdinalIgnoreCase));

        if (grainInterfacesFile is not null)
        {
            // Append to existing file
            var text = await grainInterfacesFile.GetTextAsync(cancellationToken).ConfigureAwait(false);
            var existingContent = text?.ToString() ?? "";

            var newContent = existingContent.TrimEnd();
            if (!string.IsNullOrEmpty(newContent))
            {
                newContent += NewLine + NewLine;
            }
            newContent += interfaceLine + NewLine;
            newContent += memberLines.ToString();

            var newText = Microsoft.CodeAnalysis.Text.SourceText.From(newContent, Encoding.UTF8);
            solution = solution.WithAdditionalDocumentText(grainInterfacesFile.Id, newText);
        }
        else
        {
            // Create new file with header
            var content = new StringBuilder();
            content.AppendLine("# GrainInterfaces.txt");
            content.AppendLine("# This file tracks grain interface versions for compatibility during rolling upgrades.");
            content.AppendLine("# Format:");
            content.AppendLine("#   [Alias(\"alias\")] Namespace.IInterface [Version(N)]");
            content.AppendLine("#   [Alias(\"alias\")] Namespace.IInterface.Method(params) -> ReturnType");
            content.AppendLine();
            content.AppendLine(interfaceLine);
            content.Append(memberLines);

            var newText = Microsoft.CodeAnalysis.Text.SourceText.From(content.ToString(), Encoding.UTF8);
            var projectDir = Path.GetDirectoryName(project.FilePath);
            var filePath = projectDir is not null
                ? Path.Combine(projectDir, Constants.GrainInterfacesFileName)
                : Constants.GrainInterfacesFileName;

            solution = solution.AddAdditionalDocument(
                DocumentId.CreateNewId(project.Id),
                Constants.GrainInterfacesFileName,
                newText,
                filePath: filePath);
        }

        return solution;
    }

    private static async Task<Solution> UpdateVersionInFileAsync(
        Document document,
        string interfaceName,
        string newVersion,
        CancellationToken cancellationToken)
    {
        var project = document.Project;
        var solution = project.Solution;

        var grainInterfacesFile = project.AdditionalDocuments
            .FirstOrDefault(d => Path.GetFileName(d.FilePath ?? d.Name).Equals(Constants.GrainInterfacesFileName, StringComparison.OrdinalIgnoreCase));

        if (grainInterfacesFile is null)
        {
            return solution;
        }

        var text = await grainInterfacesFile.GetTextAsync(cancellationToken).ConfigureAwait(false);
        if (text is null)
        {
            return solution;
        }

        var lines = text.ToString().Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        var newLines = new StringBuilder();

        foreach (var line in lines)
        {
            var trimmedLine = line.Trim();

            // Check if this line contains the interface declaration
            if (trimmedLine.Contains(interfaceName) && trimmedLine.Contains("[Version("))
            {
                // Replace the version number
                var versionStart = trimmedLine.IndexOf("[Version(", StringComparison.Ordinal);
                var versionEnd = trimmedLine.IndexOf(")]", versionStart, StringComparison.Ordinal);

                if (versionStart >= 0 && versionEnd > versionStart)
                {
                    var before = trimmedLine.Substring(0, versionStart);
                    var after = trimmedLine.Substring(versionEnd + 2);
                    newLines.AppendLine($"{before}[Version({newVersion})]{after}");
                    continue;
                }
            }

            newLines.AppendLine(line);
        }

        // Remove trailing newline added by AppendLine
        var newContent = newLines.ToString();
        if (newContent.EndsWith(NewLine))
        {
            newContent = newContent.Substring(0, newContent.Length - NewLine.Length);
        }

        var newText = Microsoft.CodeAnalysis.Text.SourceText.From(newContent, Encoding.UTF8);
        return solution.WithAdditionalDocumentText(grainInterfacesFile.Id, newText);
    }

    private static async Task<Solution> AddMemberToFileAsync(
        Document document,
        string interfaceName,
        string memberSignature,
        CancellationToken cancellationToken)
    {
        var project = document.Project;
        var solution = project.Solution;

        var grainInterfacesFile = project.AdditionalDocuments
            .FirstOrDefault(d => Path.GetFileName(d.FilePath ?? d.Name).Equals(Constants.GrainInterfacesFileName, StringComparison.OrdinalIgnoreCase));

        if (grainInterfacesFile is null)
        {
            return solution;
        }

        var text = await grainInterfacesFile.GetTextAsync(cancellationToken).ConfigureAwait(false);
        if (text is null)
        {
            return solution;
        }

        var lines = text.ToString().Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        var newLines = new StringBuilder();
        var foundInterface = false;
        var insertedMember = false;

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmedLine = line.Trim();

            newLines.AppendLine(line);

            // Check if this line contains the interface declaration
            if (!foundInterface && trimmedLine.Contains(interfaceName) && trimmedLine.Contains("[Version("))
            {
                foundInterface = true;
                continue;
            }

            // If we found the interface, look for where to insert the member
            if (foundInterface && !insertedMember)
            {
                // Insert before the next interface declaration or at the end of members
                var nextLine = i + 1 < lines.Length ? lines[i + 1].Trim() : "";

                // If next line is empty, a comment, or another interface, insert the member here
                if (string.IsNullOrEmpty(nextLine) ||
                    nextLine.StartsWith("#", StringComparison.Ordinal) ||
                    (nextLine.Contains("[Version(") && !nextLine.StartsWith(interfaceName, StringComparison.Ordinal)))
                {
                    newLines.AppendLine(memberSignature);
                    insertedMember = true;
                }
            }
        }

        // If we didn't insert the member yet, append it at the end
        if (foundInterface && !insertedMember)
        {
            newLines.AppendLine(memberSignature);
        }

        // Remove trailing newline added by AppendLine
        var newContent = newLines.ToString();
        if (newContent.EndsWith(NewLine))
        {
            newContent = newContent.Substring(0, newContent.Length - NewLine.Length);
        }

        var newText = Microsoft.CodeAnalysis.Text.SourceText.From(newContent, Encoding.UTF8);
        return solution.WithAdditionalDocumentText(grainInterfacesFile.Id, newText);
    }

    private static async Task<Solution> RetireInterfaceInFileAsync(
        Document document,
        string interfaceName,
        CancellationToken cancellationToken)
    {
        var project = document.Project;
        var solution = project.Solution;

        var grainInterfacesFile = project.AdditionalDocuments
            .FirstOrDefault(d => Path.GetFileName(d.FilePath ?? d.Name).Equals(Constants.GrainInterfacesFileName, StringComparison.OrdinalIgnoreCase));

        if (grainInterfacesFile is null)
        {
            return solution;
        }

        var text = await grainInterfacesFile.GetTextAsync(cancellationToken).ConfigureAwait(false);
        if (text is null)
        {
            return solution;
        }

        var lines = text.ToString().Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        var newLines = new StringBuilder();

        foreach (var line in lines)
        {
            var trimmedLine = line.Trim();

            // Check if this line contains the interface declaration
            if (trimmedLine.Contains(interfaceName) && trimmedLine.Contains("[Version(") &&
                !trimmedLine.StartsWith(GrainInterfaceVersionAnalyzer.RetiredPrefix, StringComparison.Ordinal))
            {
                // Add *RETIRED* prefix
                newLines.AppendLine($"{GrainInterfaceVersionAnalyzer.RetiredPrefix} {trimmedLine}");
                continue;
            }

            newLines.AppendLine(line);
        }

        // Remove trailing newline added by AppendLine
        var newContent = newLines.ToString();
        if (newContent.EndsWith(NewLine))
        {
            newContent = newContent.Substring(0, newContent.Length - NewLine.Length);
        }

        var newText = Microsoft.CodeAnalysis.Text.SourceText.From(newContent, Encoding.UTF8);
        return solution.WithAdditionalDocumentText(grainInterfacesFile.Id, newText);
    }

    private static ushort GetVersionFromAttributes(ISymbol symbol)
    {
        foreach (var attribute in symbol.GetAttributes())
        {
            if (string.Equals(attribute.AttributeClass?.ToDisplayString(), Constants.VersionAttributeFullyQualifiedName, StringComparison.Ordinal))
            {
                if (attribute.ConstructorArguments.Length > 0 &&
                    attribute.ConstructorArguments[0].Value is ushort version)
                {
                    return version;
                }
            }
        }

        return 0;
    }

    private static string? GetAliasFromAttributes(ISymbol symbol)
    {
        foreach (var attribute in symbol.GetAttributes())
        {
            if (string.Equals(attribute.AttributeClass?.ToDisplayString(), Constants.AliasAttributeFullyQualifiedName, StringComparison.Ordinal))
            {
                if (attribute.ConstructorArguments.Length > 0 &&
                    attribute.ConstructorArguments[0].Value is string alias)
                {
                    return alias;
                }
            }
        }

        return null;
    }

    private static string GetMethodSignature(IMethodSymbol method)
    {
        var sb = new StringBuilder();
        sb.Append(method.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", ""));
        sb.Append('.');
        sb.Append(method.Name);
        sb.Append('(');

        for (int i = 0; i < method.Parameters.Length; i++)
        {
            if (i > 0) sb.Append(", ");
            var param = method.Parameters[i];
            sb.Append(param.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));
            sb.Append(' ');
            sb.Append(param.Name);
        }

        sb.Append(')');
        sb.Append(" -> ");
        sb.Append(method.ReturnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));

        return sb.ToString();
    }
}
