using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using System;
using System.Collections.Immutable;

namespace Orleans.Analyzers;

/// <summary>
/// An analyzer that warns when grain code uses ConfigureAwait(false) or ConfigureAwait(ConfigureAwaitOptions)
/// without the ContinueOnCapturedContext flag.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class ConfigureAwaitAnalyzer : DiagnosticAnalyzer
{
    public const string RuleId = "ORLEANS0014";

    private static readonly LocalizableString Title = new LocalizableResourceString(
        nameof(Resources.AvoidConfigureAwaitFalseInGrainTitle),
        Resources.ResourceManager,
        typeof(Resources));

    private static readonly LocalizableString MessageFormat = new LocalizableResourceString(
        nameof(Resources.AvoidConfigureAwaitFalseInGrainMessageFormat),
        Resources.ResourceManager,
        typeof(Resources));

    private static readonly LocalizableString Description = new LocalizableResourceString(
        nameof(Resources.AvoidConfigureAwaitFalseInGrainDescription),
        Resources.ResourceManager,
        typeof(Resources));

    private static readonly DiagnosticDescriptor Rule = new(
        id: RuleId,
        title: Title,
        messageFormat: MessageFormat,
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: Description);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);
        context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
    }

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;

        // Check if this is a ConfigureAwait call
        if (!IsConfigureAwaitCall(invocation, out var methodName))
        {
            return;
        }

        // Check if this code is inside a grain class
        if (!IsInsideGrainClass(invocation, context.SemanticModel))
        {
            return;
        }

        // Get the symbol for the invocation to analyze the argument
        var symbolInfo = context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken);
        if (symbolInfo.Symbol is not IMethodSymbol methodSymbol)
        {
            return;
        }

        // Only check ConfigureAwait method
        if (!string.Equals(methodSymbol.Name, "ConfigureAwait", StringComparison.Ordinal))
        {
            return;
        }

        // Check if it's a ConfigureAwait method on a Task-like type
        var containingType = methodSymbol.ContainingType;
        if (!IsTaskLikeType(containingType))
        {
            return;
        }

        // Get the arguments
        var arguments = invocation.ArgumentList?.Arguments;
        if (arguments is null || arguments.Value.Count == 0)
        {
            return;
        }

        var firstArgument = arguments.Value[0];
        var argumentType = context.SemanticModel.GetTypeInfo(firstArgument.Expression, context.CancellationToken).Type;

        if (argumentType is null)
        {
            return;
        }

        // Check for ConfigureAwait(bool) overload
        if (argumentType.SpecialType == SpecialType.System_Boolean)
        {
            var constantValue = context.SemanticModel.GetConstantValue(firstArgument.Expression, context.CancellationToken);
            if (constantValue.HasValue && constantValue.Value is false)
            {
                // ConfigureAwait(false) is not allowed
                context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation()));
            }
            return;
        }

        // Check for ConfigureAwait(ConfigureAwaitOptions) overload
        if (IsConfigureAwaitOptionsType(argumentType))
        {
            if (!HasContinueOnCapturedContextFlag(firstArgument.Expression, context.SemanticModel, context.CancellationToken))
            {
                context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation()));
            }
        }
    }

    private static bool IsConfigureAwaitCall(InvocationExpressionSyntax invocation, out string methodName)
    {
        methodName = null;

        if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
        {
            methodName = memberAccess.Name.Identifier.Text;
            return string.Equals(methodName, "ConfigureAwait", StringComparison.Ordinal);
        }

        return false;
    }

    private static bool IsInsideGrainClass(SyntaxNode node, SemanticModel semanticModel)
    {
        // Walk up to find the containing type declaration
        var current = node.Parent;
        while (current is not null)
        {
            if (current is ClassDeclarationSyntax classDeclaration)
            {
                var typeSymbol = semanticModel.GetDeclaredSymbol(classDeclaration);
                if (typeSymbol is INamedTypeSymbol namedTypeSymbol && namedTypeSymbol.IsGrainClass())
                {
                    return true;
                }
            }
            else if (current is StructDeclarationSyntax or RecordDeclarationSyntax)
            {
                // If we hit a struct or record before finding a grain class, we're not in a grain
                // (structs and records can't be grains)
                return false;
            }

            current = current.Parent;
        }

        return false;
    }

    private static bool IsTaskLikeType(INamedTypeSymbol type)
    {
        if (type is null)
        {
            return false;
        }

        var fullName = type.ToDisplayString(NullableFlowState.None);

        // Check for common task-like types that have ConfigureAwait
        return fullName.StartsWith("System.Threading.Tasks.Task", StringComparison.Ordinal)
            || fullName.StartsWith("System.Threading.Tasks.ValueTask", StringComparison.Ordinal)
            || fullName.StartsWith("System.Runtime.CompilerServices.ConfiguredTaskAwaitable", StringComparison.Ordinal)
            || fullName.StartsWith("System.Runtime.CompilerServices.ConfiguredValueTaskAwaitable", StringComparison.Ordinal)
            || fullName.StartsWith("System.Collections.Generic.IAsyncEnumerable", StringComparison.Ordinal)
            || fullName.StartsWith("System.Runtime.CompilerServices.ConfiguredCancelableAsyncEnumerable", StringComparison.Ordinal);
    }

    private static bool IsConfigureAwaitOptionsType(ITypeSymbol type)
    {
        if (type is null)
        {
            return false;
        }

        return string.Equals(
            type.ToDisplayString(NullableFlowState.None),
            "System.Threading.Tasks.ConfigureAwaitOptions",
            StringComparison.Ordinal);
    }

    private static bool HasContinueOnCapturedContextFlag(ExpressionSyntax expression, SemanticModel semanticModel, System.Threading.CancellationToken cancellationToken)
    {
        // ConfigureAwaitOptions.ContinueOnCapturedContext has value 1
        const int ContinueOnCapturedContextValue = 1;

        // Try to get the constant value
        var constantValue = semanticModel.GetConstantValue(expression, cancellationToken);
        if (constantValue.HasValue && constantValue.Value is int intValue)
        {
            // Check if ContinueOnCapturedContext flag (value 1) is set
            return (intValue & ContinueOnCapturedContextValue) != 0;
        }

        // If we can't determine the value at compile time, we need to analyze the expression
        // to check if it includes ContinueOnCapturedContext
        return ExpressionIncludesContinueOnCapturedContext(expression, semanticModel, cancellationToken);
    }

    private static bool ExpressionIncludesContinueOnCapturedContext(ExpressionSyntax expression, SemanticModel semanticModel, System.Threading.CancellationToken cancellationToken)
    {
        // Handle member access like ConfigureAwaitOptions.ContinueOnCapturedContext
        if (expression is MemberAccessExpressionSyntax memberAccess)
        {
            var memberName = memberAccess.Name.Identifier.Text;
            if (string.Equals(memberName, "ContinueOnCapturedContext", StringComparison.Ordinal))
            {
                return true;
            }
        }

        // Handle binary OR expressions like ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.ForceYielding
        if (expression is BinaryExpressionSyntax binaryExpression &&
            binaryExpression.IsKind(SyntaxKind.BitwiseOrExpression))
        {
            return ExpressionIncludesContinueOnCapturedContext(binaryExpression.Left, semanticModel, cancellationToken)
                || ExpressionIncludesContinueOnCapturedContext(binaryExpression.Right, semanticModel, cancellationToken);
        }

        // Handle parenthesized expressions
        if (expression is ParenthesizedExpressionSyntax parenthesized)
        {
            return ExpressionIncludesContinueOnCapturedContext(parenthesized.Expression, semanticModel, cancellationToken);
        }

        // Handle cast expressions
        if (expression is CastExpressionSyntax castExpression)
        {
            return ExpressionIncludesContinueOnCapturedContext(castExpression.Expression, semanticModel, cancellationToken);
        }

        // If we encounter a variable or method call, we can't statically determine the flags
        // In this case, we give the benefit of the doubt and don't report
        if (expression is IdentifierNameSyntax or InvocationExpressionSyntax)
        {
            // Try to get the constant value as a fallback
            var constantValue = semanticModel.GetConstantValue(expression, cancellationToken);
            if (constantValue.HasValue && constantValue.Value is int intValue)
            {
                const int ContinueOnCapturedContextValue = 1;
                return (intValue & ContinueOnCapturedContextValue) != 0;
            }

            // Can't determine - don't report false positives
            return true;
        }

        return false;
    }
}
