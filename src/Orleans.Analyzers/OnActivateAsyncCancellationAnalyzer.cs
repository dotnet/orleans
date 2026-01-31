using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Orleans.Analyzers
{
    /// <summary>
    /// Analyzer that ensures proper cancellation token propagation in <c>OnActivateAsync</c> implementations.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This analyzer checks implementations of <c>IGrainBase.OnActivateAsync</c> to ensure that the cancellation token
    /// parameter is properly propagated to awaited methods. This is important for ensuring that grain activation
    /// can be properly cancelled when needed.
    /// </para>
    /// <para>
    /// The analyzer reports two types of diagnostics:
    /// <list type="bullet">
    /// <item>
    /// <term>ORLEANS0014</term>
    /// <description>When an awaited method has an overload that accepts a <see cref="System.Threading.CancellationToken"/>
    /// but the current call doesn't use it.</description>
    /// </item>
    /// <item>
    /// <term>ORLEANS0015</term>
    /// <description>When an awaited method doesn't have a <see cref="System.Threading.CancellationToken"/> overload
    /// or explicitly passes <see cref="System.Threading.CancellationToken.None"/>.</description>
    /// </item>
    /// </list>
    /// </para>
    /// </remarks>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public class OnActivateAsyncCancellationAnalyzer : DiagnosticAnalyzer
    {
        public const string MissingCancellationTokenOverloadDiagnosticId = "ORLEANS0014";
        public const string MissingCancellationTokenOverloadTitle = "Awaited method should use cancellation token overload";
        public const string MissingCancellationTokenOverloadMessageFormat = "The awaited method '{0}' has an overload that accepts a CancellationToken. Use the cancellation token from the OnActivateAsync parameter or explicitly pass CancellationToken.None.";
        public const string MissingCancellationTokenOverloadDescription = "Awaited methods in OnActivateAsync should propagate the cancellation token to ensure proper cancellation support during grain activation.";

        public const string MissingWaitAsyncDiagnosticId = "ORLEANS0015";
        public const string MissingWaitAsyncTitle = "Awaited method should use WaitAsync with cancellation token";
        public const string MissingWaitAsyncMessageFormat = "The awaited method '{0}' does not accept a CancellationToken. Use .WaitAsync(cancellationToken) to support cancellation.";
        public const string MissingWaitAsyncDescription = "Awaited methods in OnActivateAsync that don't support CancellationToken should use .WaitAsync(cancellationToken) to ensure proper cancellation support during grain activation.";

        public const string Category = "Usage";

        private const string IGrainBaseFullyQualifiedName = "Orleans.IGrainBase";
        private const string OnActivateAsyncMethodName = "OnActivateAsync";
        private const string CancellationTokenFullyQualifiedName = "System.Threading.CancellationToken";
        private const string WaitAsyncMethodName = "WaitAsync";
        private const string ConfigureAwaitMethodName = "ConfigureAwait";

        private static readonly DiagnosticDescriptor MissingCancellationTokenOverloadRule = new DiagnosticDescriptor(
            MissingCancellationTokenOverloadDiagnosticId,
            MissingCancellationTokenOverloadTitle,
            MissingCancellationTokenOverloadMessageFormat,
            Category,
            DiagnosticSeverity.Info,
            isEnabledByDefault: true,
            description: MissingCancellationTokenOverloadDescription);

        private static readonly DiagnosticDescriptor MissingWaitAsyncRule = new DiagnosticDescriptor(
            MissingWaitAsyncDiagnosticId,
            MissingWaitAsyncTitle,
            MissingWaitAsyncMessageFormat,
            Category,
            DiagnosticSeverity.Info,
            isEnabledByDefault: true,
            description: MissingWaitAsyncDescription);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
            ImmutableArray.Create(MissingCancellationTokenOverloadRule, MissingWaitAsyncRule);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);
            context.EnableConcurrentExecution();
            context.RegisterSyntaxNodeAction(AnalyzeMethodDeclaration, SyntaxKind.MethodDeclaration);
        }

        private static void AnalyzeMethodDeclaration(SyntaxNodeAnalysisContext context)
        {
            if (!(context.Node is MethodDeclarationSyntax methodSyntax))
            {
                return;
            }

            var methodSymbol = context.SemanticModel.GetDeclaredSymbol(methodSyntax, context.CancellationToken);
            if (methodSymbol == null)
            {
                return;
            }

            // Check if this is an implementation of IGrainBase.OnActivateAsync
            if (!IsOnActivateAsyncImplementation(methodSymbol, context.Compilation))
            {
                return;
            }

            // Find the CancellationToken parameter
            var cancellationTokenParameter = FindCancellationTokenParameter(methodSymbol, context.Compilation);
            if (cancellationTokenParameter == null)
            {
                return;
            }

            // Analyze all await expressions in the method body
            var awaitExpressions = methodSyntax.DescendantNodes().OfType<AwaitExpressionSyntax>();

            foreach (var awaitExpression in awaitExpressions)
            {
                AnalyzeAwaitExpression(context, awaitExpression, cancellationTokenParameter);
            }
        }

        private static bool IsOnActivateAsyncImplementation(IMethodSymbol methodSymbol, Compilation compilation)
        {
            if (methodSymbol.Name != OnActivateAsyncMethodName)
            {
                return false;
            }

            var iGrainBaseType = compilation.GetTypeByMetadataName(IGrainBaseFullyQualifiedName);
            if (iGrainBaseType == null)
            {
                return false;
            }

            var containingType = methodSymbol.ContainingType;
            if (containingType == null)
            {
                return false;
            }

            // Check if the containing type implements IGrainBase
            var implementsIGrainBase = containingType.AllInterfaces.Any(i =>
                SymbolEqualityComparer.Default.Equals(i, iGrainBaseType));

            if (!implementsIGrainBase)
            {
                return false;
            }

            // Check if this method matches the signature of IGrainBase.OnActivateAsync
            var cancellationTokenType = compilation.GetTypeByMetadataName(CancellationTokenFullyQualifiedName);
            if (cancellationTokenType == null)
            {
                return false;
            }

            // OnActivateAsync should have exactly one parameter of type CancellationToken
            if (methodSymbol.Parameters.Length != 1)
            {
                return false;
            }

            return SymbolEqualityComparer.Default.Equals(methodSymbol.Parameters[0].Type, cancellationTokenType);
        }

        private static IParameterSymbol FindCancellationTokenParameter(IMethodSymbol methodSymbol, Compilation compilation)
        {
            var cancellationTokenType = compilation.GetTypeByMetadataName(CancellationTokenFullyQualifiedName);
            if (cancellationTokenType == null)
            {
                return null;
            }

            return methodSymbol.Parameters.FirstOrDefault(p =>
                SymbolEqualityComparer.Default.Equals(p.Type, cancellationTokenType));
        }

        private static void AnalyzeAwaitExpression(
            SyntaxNodeAnalysisContext context,
            AwaitExpressionSyntax awaitExpression,
            IParameterSymbol cancellationTokenParameter)
        {
            var expression = awaitExpression.Expression;
            if (expression == null)
            {
                return;
            }

            // Unwrap ConfigureAwait calls to get the actual method being awaited
            var targetExpression = UnwrapConfigureAwait(expression, context.SemanticModel);

            // Check if the target is a WaitAsync call
            if (IsWaitAsyncCall(targetExpression, context.SemanticModel))
            {
                return;
            }

            // Get the invocation expression
            var invocationExpression = targetExpression as InvocationExpressionSyntax;
            if (invocationExpression == null)
            {
                // Check if this is a variable - if so, try to find where it was assigned
                var identifierName = targetExpression as IdentifierNameSyntax;
                if (identifierName != null)
                {
                    var assignedInvocation = TryGetAssignedInvocation(identifierName, context);
                    if (assignedInvocation != null)
                    {
                        // Analyze the original method call that was assigned to this variable
                        AnalyzeInvocationExpression(context, assignedInvocation, cancellationTokenParameter, awaitExpression.GetLocation());
                        return;
                    }
                }

                // For non-invocation expressions (like properties returning Task or local variables we can't trace)
                var typeInfo = context.SemanticModel.GetTypeInfo(targetExpression, context.CancellationToken);
                if (typeInfo.Type != null && IsAwaitableType(typeInfo.Type))
                {
                    var diagnostic = Diagnostic.Create(
                        MissingWaitAsyncRule,
                        targetExpression.GetLocation(),
                        targetExpression.ToString());
                    context.ReportDiagnostic(diagnostic);
                }
                return;
            }

            AnalyzeInvocationExpression(context, invocationExpression, cancellationTokenParameter, invocationExpression.GetLocation());
        }

        /// <summary>
        /// Tries to find the invocation expression that was assigned to a local variable.
        /// </summary>
        private static InvocationExpressionSyntax TryGetAssignedInvocation(
            IdentifierNameSyntax identifierName,
            SyntaxNodeAnalysisContext context)
        {
            var symbolInfo = context.SemanticModel.GetSymbolInfo(identifierName, context.CancellationToken);
            var localSymbol = symbolInfo.Symbol as ILocalSymbol;
            if (localSymbol == null)
            {
                return null;
            }

            // Find the declaration of this local variable
            foreach (var syntaxRef in localSymbol.DeclaringSyntaxReferences)
            {
                var declaratorSyntax = syntaxRef.GetSyntax(context.CancellationToken) as VariableDeclaratorSyntax;
                if (declaratorSyntax != null && declaratorSyntax.Initializer != null)
                {
                    var initializerValue = declaratorSyntax.Initializer.Value;
                    
                    // Unwrap ConfigureAwait if present
                    initializerValue = UnwrapConfigureAwait(initializerValue, context.SemanticModel);
                    
                    var invocation = initializerValue as InvocationExpressionSyntax;
                    if (invocation != null)
                    {
                        return invocation;
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Analyzes an invocation expression to determine if it needs a CancellationToken.
        /// </summary>
        private static void AnalyzeInvocationExpression(
            SyntaxNodeAnalysisContext context,
            InvocationExpressionSyntax invocationExpression,
            IParameterSymbol cancellationTokenParameter,
            Location diagnosticLocation)
        {
            // Get the method symbol being invoked
            var symbolInfo = context.SemanticModel.GetSymbolInfo(invocationExpression, context.CancellationToken);
            if (!(symbolInfo.Symbol is IMethodSymbol invokedMethod))
            {
                return;
            }

            // Get all arguments being passed to the method
            var arguments = invocationExpression.ArgumentList.Arguments;

            // Check if the method receives the OnActivateAsync cancellation token parameter
            var cancellationTokenType = context.Compilation.GetTypeByMetadataName(CancellationTokenFullyQualifiedName);
            var tokenPassedStatus = GetCancellationTokenPassedStatus(
                arguments,
                context.SemanticModel,
                cancellationTokenType,
                cancellationTokenParameter);

            switch (tokenPassedStatus)
            {
                case CancellationTokenPassedStatus.CorrectTokenPassed:
                    // The OnActivateAsync cancellation token is being passed - no diagnostic needed
                    return;

                case CancellationTokenPassedStatus.DifferentTokenPassed:
                    // A CancellationToken is passed but it's not the OnActivateAsync parameter
                    // Need to use .WaitAsync(cancellationToken) to ensure proper cancellation
                    var differentTokenDiagnostic = Diagnostic.Create(
                        MissingWaitAsyncRule,
                        diagnosticLocation,
                        invokedMethod.Name);
                    context.ReportDiagnostic(differentTokenDiagnostic);
                    return;

                case CancellationTokenPassedStatus.NoTokenPassed:
                    // No CancellationToken is passed - check if we can pass one or need WaitAsync
                    break;
            }

            // Check if the current method has an optional CancellationToken parameter that wasn't passed
            if (cancellationTokenType != null && MethodHasOptionalCancellationTokenParameter(invokedMethod, cancellationTokenType))
            {
                var diagnostic = Diagnostic.Create(
                    MissingCancellationTokenOverloadRule,
                    diagnosticLocation,
                    invokedMethod.Name);
                context.ReportDiagnostic(diagnostic);
                return;
            }

            // Check if there's an overload that accepts CancellationToken
            var hasOverloadWithCancellationToken = HasOverloadWithCancellationToken(
                invokedMethod,
                context.Compilation);

            if (hasOverloadWithCancellationToken)
            {
                var diagnostic = Diagnostic.Create(
                    MissingCancellationTokenOverloadRule,
                    diagnosticLocation,
                    invokedMethod.Name);
                context.ReportDiagnostic(diagnostic);
            }
            else
            {
                var diagnostic = Diagnostic.Create(
                    MissingWaitAsyncRule,
                    diagnosticLocation,
                    invokedMethod.Name);
                context.ReportDiagnostic(diagnostic);
            }
        }

        /// <summary>
        /// Checks if a method has an optional CancellationToken parameter that can be passed.
        /// </summary>
        private static bool MethodHasOptionalCancellationTokenParameter(IMethodSymbol method, ITypeSymbol cancellationTokenType)
        {
            foreach (var parameter in method.Parameters)
            {
                if (SymbolEqualityComparer.Default.Equals(parameter.Type, cancellationTokenType) &&
                    parameter.HasExplicitDefaultValue)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Unwraps ConfigureAwait calls to get the actual expression being configured.
        /// For example: "Task.Delay(100).ConfigureAwait(false)" returns "Task.Delay(100)"
        /// </summary>
        private static ExpressionSyntax UnwrapConfigureAwait(ExpressionSyntax expression, SemanticModel semanticModel)
        {
            var invocation = expression as InvocationExpressionSyntax;
            if (invocation == null)
            {
                return expression;
            }

            var memberAccess = invocation.Expression as MemberAccessExpressionSyntax;
            if (memberAccess == null)
            {
                return expression;
            }

            // Check if this is a ConfigureAwait call
            if (memberAccess.Name.Identifier.Text == ConfigureAwaitMethodName)
            {
                // Return the expression that ConfigureAwait is called on
                return memberAccess.Expression;
            }

            return expression;
        }


        private static bool IsWaitAsyncCall(ExpressionSyntax expression, SemanticModel semanticModel)
        {
            var invocation = expression as InvocationExpressionSyntax;
            if (invocation == null)
            {
                return false;
            }

            var memberAccess = invocation.Expression as MemberAccessExpressionSyntax;
            if (memberAccess != null && memberAccess.Name.Identifier.Text == WaitAsyncMethodName)
            {
                return true;
            }

            // Also check the symbol to be sure
            var symbolInfo = semanticModel.GetSymbolInfo(invocation);
            var methodSymbol = symbolInfo.Symbol as IMethodSymbol;
            if (methodSymbol != null && methodSymbol.Name == WaitAsyncMethodName)
            {
                return true;
            }

            return false;
        }

        private static bool IsAwaitableType(ITypeSymbol type)
        {
            var typeName = type.ToDisplayString();
            return typeName.StartsWith("System.Threading.Tasks.Task") ||
                   typeName.StartsWith("System.Threading.Tasks.ValueTask");
        }

        /// <summary>
        /// Represents the status of CancellationToken being passed to a method.
        /// </summary>
        private enum CancellationTokenPassedStatus
        {
            /// <summary>No CancellationToken argument is passed.</summary>
            NoTokenPassed,
            /// <summary>The correct OnActivateAsync CancellationToken parameter is passed.</summary>
            CorrectTokenPassed,
            /// <summary>A CancellationToken is passed but it's not the OnActivateAsync parameter.</summary>
            DifferentTokenPassed
        }

        /// <summary>
        /// Checks if a CancellationToken is being passed to the method and whether it's the correct one.
        /// </summary>
        private static CancellationTokenPassedStatus GetCancellationTokenPassedStatus(
            SeparatedSyntaxList<ArgumentSyntax> arguments,
            SemanticModel semanticModel,
            ITypeSymbol cancellationTokenType,
            IParameterSymbol expectedParameter)
        {
            if (cancellationTokenType == null)
            {
                return CancellationTokenPassedStatus.NoTokenPassed;
            }

            foreach (var argument in arguments)
            {
                var typeInfo = semanticModel.GetTypeInfo(argument.Expression);
                if (typeInfo.Type != null &&
                    SymbolEqualityComparer.Default.Equals(typeInfo.Type, cancellationTokenType))
                {
                    // A CancellationToken is being passed - check if it's the expected parameter
                    if (IsExpectedCancellationTokenParameter(argument.Expression, semanticModel, expectedParameter))
                    {
                        return CancellationTokenPassedStatus.CorrectTokenPassed;
                    }
                    else
                    {
                        return CancellationTokenPassedStatus.DifferentTokenPassed;
                    }
                }
            }

            return CancellationTokenPassedStatus.NoTokenPassed;
        }

        /// <summary>
        /// Checks if the expression is the expected CancellationToken parameter from OnActivateAsync.
        /// </summary>
        private static bool IsExpectedCancellationTokenParameter(
            ExpressionSyntax expression,
            SemanticModel semanticModel,
            IParameterSymbol expectedParameter)
        {
            if (expectedParameter == null)
            {
                return false;
            }

            var symbolInfo = semanticModel.GetSymbolInfo(expression);
            
            // Direct parameter reference
            var parameterSymbol = symbolInfo.Symbol as IParameterSymbol;
            if (parameterSymbol != null)
            {
                return SymbolEqualityComparer.Default.Equals(parameterSymbol, expectedParameter);
            }

            // Could also be a local variable that was assigned from the parameter
            // For simplicity, we only accept direct parameter references
            // Any other CancellationToken (variables, CancellationToken.None, default, etc.) 
            // will be treated as "different token"
            return false;
        }

        private static bool HasOverloadWithCancellationToken(IMethodSymbol method, Compilation compilation)
        {
            var cancellationTokenType = compilation.GetTypeByMetadataName(CancellationTokenFullyQualifiedName);
            if (cancellationTokenType == null)
            {
                return false;
            }

            var containingType = method.ContainingType;
            if (containingType == null)
            {
                return false;
            }

            var methodsWithSameName = containingType.GetMembers(method.Name).OfType<IMethodSymbol>();

            foreach (var overload in methodsWithSameName)
            {
                // Skip the current method
                if (SymbolEqualityComparer.Default.Equals(overload, method))
                {
                    continue;
                }

                // Check if this overload has a CancellationToken parameter
                if (overload.Parameters.Any(p => SymbolEqualityComparer.Default.Equals(p.Type, cancellationTokenType)))
                {
                    // Verify that this overload is compatible
                    if (IsCompatibleOverload(method, overload, cancellationTokenType))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool IsCompatibleOverload(IMethodSymbol original, IMethodSymbol overload, ITypeSymbol cancellationTokenType)
        {
            var originalParams = original.Parameters;
            var overloadParams = overload.Parameters;

            // Simple case: overload has exactly one more parameter which is CancellationToken
            if (overloadParams.Length == originalParams.Length + 1)
            {
                var lastParam = overloadParams[overloadParams.Length - 1];
                if (!SymbolEqualityComparer.Default.Equals(lastParam.Type, cancellationTokenType))
                {
                    return false;
                }

                // Check that all other parameters match
                for (int i = 0; i < originalParams.Length; i++)
                {
                    if (!SymbolEqualityComparer.Default.Equals(originalParams[i].Type, overloadParams[i].Type))
                    {
                        return false;
                    }
                }

                return true;
            }

            // Case: same method signature but with optional CancellationToken parameter
            // For example: DoWorkAsync(int value = 0, CancellationToken ct = default)
            // The method being called is the same method, just not passing the CancellationToken
            if (SymbolEqualityComparer.Default.Equals(original, overload))
            {
                return false; // Same method, not an overload
            }

            // Check if this is a method with optional parameters that includes CancellationToken
            // This handles the case where the method has default values
            var overloadCancellationTokenParams = overloadParams.Where(p =>
                SymbolEqualityComparer.Default.Equals(p.Type, cancellationTokenType) &&
                p.HasExplicitDefaultValue).ToArray();

            if (overloadCancellationTokenParams.Length > 0)
            {
                // The overload has an optional CancellationToken parameter
                // Check if the non-CancellationToken parameters are compatible
                var overloadNonCtParams = overloadParams.Where(p =>
                    !SymbolEqualityComparer.Default.Equals(p.Type, cancellationTokenType)).ToArray();

                if (overloadNonCtParams.Length == originalParams.Length)
                {
                    bool allMatch = true;
                    for (int i = 0; i < originalParams.Length; i++)
                    {
                        if (!SymbolEqualityComparer.Default.Equals(originalParams[i].Type, overloadNonCtParams[i].Type))
                        {
                            allMatch = false;
                            break;
                        }
                    }
                    return allMatch;
                }
            }

            return false;
        }
    }
}
