using System;
using Microsoft.CodeAnalysis;

namespace Orleans.CodeGenerator.Diagnostics;

public static class UnhandledCodeGenerationExceptionDiagnostic
{
    public const string RuleId = "ORLEANS0103"; 
    private const string Category = "Usage";
    private static readonly LocalizableString Title = "An unhandled source generation exception occurred";
    private static readonly LocalizableString MessageFormat = "An unhandled exception occurred while generating source for your project: {0}";
    private static readonly LocalizableString Description = "Please report this bug by opening an issue https://github.com/dotnet/orleans/issues/new.";

    internal static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(RuleId, Title, MessageFormat, Category, DiagnosticSeverity.Error, isEnabledByDefault: true, description: Description);

    internal static Diagnostic CreateDiagnostic(Exception exception) => Diagnostic.Create(Rule, location: null, messageArgs: new[] { exception.ToString() });
}