namespace Orleans.CodeGenerator
{
    using System;
    using System.CodeDom.Compiler;
    using System.Collections.Generic;
    using System.Linq;
    using System.Reflection;
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CSharp;
    using Microsoft.CodeAnalysis.CSharp.Syntax;
    using Orleans.CodeGeneration;
    using Orleans.CodeGenerator.Utilities;
    using Orleans.Runtime;
    using GrainInterfaceUtils = Orleans.CodeGeneration.GrainInterfaceUtils;
    using SF = Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

    /// <summary>
    /// Methods common to multiple code generators.
    /// </summary>
    internal static class CodeGeneratorCommon
    {
        /// <summary>
        /// The name of these code generators.
        /// </summary>
        public const string ToolName = "Orleans-CodeGenerator";

        /// <summary>
        /// The prefix for class names.
        /// </summary>
        internal const string ClassPrefix = "OrleansCodeGen";

        /// <summary>
        /// The current version.
        /// </summary>
        private static readonly string CodeGeneratorVersion = RuntimeVersion.FileVersion;

        public static CompilationUnitSyntax AddGeneratedCodeAttribute(GeneratedSyntax generatedSyntax)
        {
            var codeGenTargetAttributes =
                SF.AttributeList()
                    .AddAttributes(
                        generatedSyntax.SourceAssemblies.Select(
                            asm =>
                            SF.Attribute(typeof(OrleansCodeGenerationTargetAttribute).GetNameSyntax())
                                .AddArgumentListArguments(
                                    SF.AttributeArgument(asm.GetName().FullName.GetLiteralExpression()))).ToArray())
                    .WithTarget(SF.AttributeTargetSpecifier(SF.Token(SyntaxKind.AssemblyKeyword)));
            var generatedCodeAttribute =
                SF.AttributeList()
                    .AddAttributes(
                        SF.Attribute(typeof(GeneratedCodeAttribute).GetNameSyntax())
                            .AddArgumentListArguments(
                                SF.AttributeArgument(ToolName.GetLiteralExpression()),
                                SF.AttributeArgument(RuntimeVersion.FileVersion.GetLiteralExpression())))
                    .WithTarget(SF.AttributeTargetSpecifier(SF.Token(SyntaxKind.AssemblyKeyword)));
            return generatedSyntax.Syntax.AddAttributeLists(generatedCodeAttribute, codeGenTargetAttributes);
        }

        internal static AttributeSyntax GetGeneratedCodeAttributeSyntax()
        {
            return
                SF.Attribute(typeof(GeneratedCodeAttribute).GetNameSyntax())
                    .AddArgumentListArguments(
                        SF.AttributeArgument(ToolName.GetLiteralExpression()),
                        SF.AttributeArgument(CodeGeneratorVersion.GetLiteralExpression()));
        }

        internal static string GenerateSourceCode(CompilationUnitSyntax code)
        {
            var syntax = code.NormalizeWhitespace();
            var source = syntax.ToFullString();
            return source;
        }

        /// <summary>
        /// Generates switch cases for the provided grain type.
        /// </summary>
        /// <param name="grainType">
        /// The grain type.
        /// </param>
        /// <param name="methodIdArgument">
        /// The method id argument, which is used to select the correct switch label.
        /// </param>
        /// <param name="generateMethodHandler">
        /// The function used to generate switch block statements for each method.
        /// </param>
        /// <returns>
        /// The switch cases for the provided grain type.
        /// </returns>
        public static SwitchSectionSyntax[] GenerateGrainInterfaceAndMethodSwitch(
            Type grainType,
            ExpressionSyntax methodIdArgument,
            Func<MethodInfo, StatementSyntax[]> generateMethodHandler)
        {
            var interfaces = GrainInterfaceUtils.GetRemoteInterfaces(grainType);
            interfaces[GrainInterfaceUtils.GetGrainInterfaceId(grainType)] = grainType;

            // Switch on interface id.
            var interfaceCases = new List<SwitchSectionSyntax>();
            foreach (var @interface in interfaces)
            {
                var interfaceType = @interface.Value;
                var interfaceId = @interface.Key;
                var methods = GrainInterfaceUtils.GetMethods(interfaceType);

                var methodCases = new List<SwitchSectionSyntax>();

                // Switch on method id.
                foreach (var method in methods)
                {
                    // Generate switch case.
                    var methodId = GrainInterfaceUtils.ComputeMethodId(method);
                    var methodType = method;

                    // Generate the switch label for this interface id.
                    var methodIdSwitchLabel =
                        SF.CaseSwitchLabel(
                            SF.LiteralExpression(SyntaxKind.NumericLiteralExpression, SF.Literal(methodId)));

                    // Generate the switch body.
                    var methodInvokeStatement = generateMethodHandler(methodType);

                    methodCases.Add(
                        SF.SwitchSection().AddLabels(methodIdSwitchLabel).AddStatements(methodInvokeStatement));
                }

                // Generate the switch label for this interface id.
                var interfaceIdSwitchLabel =
                    SF.CaseSwitchLabel(
                        SF.LiteralExpression(SyntaxKind.NumericLiteralExpression, SF.Literal(interfaceId)));

                // Generate the default case, which will throw a NotImplementedException.
                var errorMessage = SF.BinaryExpression(
                    SyntaxKind.AddExpression,
                    "interfaceId=".GetLiteralExpression(),
                    SF.BinaryExpression(
                        SyntaxKind.AddExpression,
                        SF.LiteralExpression(SyntaxKind.NumericLiteralExpression, SF.Literal(interfaceId)),
                        SF.BinaryExpression(
                            SyntaxKind.AddExpression,
                            ",methodId=".GetLiteralExpression(),
                            methodIdArgument)));
                var throwStatement =
                    SF.ThrowStatement(
                        SF.ObjectCreationExpression(typeof(NotImplementedException).GetTypeSyntax())
                            .AddArgumentListArguments(SF.Argument(errorMessage)));
                var defaultCase = SF.SwitchSection().AddLabels(SF.DefaultSwitchLabel()).AddStatements(throwStatement);

                // Generate switch statements for the methods in this interface.
                var methodSwitchStatements =
                    SF.SwitchStatement(methodIdArgument).AddSections(methodCases.ToArray()).AddSections(defaultCase);

                // Generate the switch section for this interface.
                interfaceCases.Add(
                    SF.SwitchSection().AddLabels(interfaceIdSwitchLabel).AddStatements(methodSwitchStatements));
            }

            return interfaceCases.ToArray();
        }

        public static string GetRandomNamespace()
        {
            return "Generated" + DateTime.Now.Ticks.ToString("X");
        }

        public static string GetGeneratedNamespace(Type type, bool randomize = false)
        {
            string result;
            if (randomize || string.IsNullOrWhiteSpace(type.Namespace))
            {
                result = GetRandomNamespace();
            }
            else
            {
                result = type.Namespace;
            }

            return result;
        }
    }
}
