namespace Orleans.CodeGenerator
{
    using System;
    using System.CodeDom.Compiler;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CSharp;
    using Microsoft.CodeAnalysis.CSharp.Syntax;

    using Orleans;
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
        private const string CodeGeneratorName = "Orleans-CodeGenerator";

        /// <summary>
        /// The prefix for class names.
        /// </summary>
        internal const string ClassPrefix = "OrleansCodeGen";

        /// <summary>
        /// The current version.
        /// </summary>
        private static readonly string CodeGeneratorVersion = RuntimeVersion.FileVersion;

        /// <summary>
        /// Generates and compiles an assembly for the provided grains.
        /// </summary>
        /// <param name="generatedSyntax">
        /// The generated code.
        /// </param>
        /// <param name="assemblyName">
        /// The name for the generated assembly.
        /// </param>
        /// <param name="emitDebugSymbols">
        /// Whether or not to emit debug symbols for the generated assembly.
        /// </param>
        /// <returns>
        /// The raw assembly.
        /// </returns>
        /// <exception cref="CodeGenerationException">
        /// An error occurred generating code.
        /// </exception>
        public static GeneratedAssembly CompileAssembly(GeneratedSyntax generatedSyntax, string assemblyName, bool emitDebugSymbols)
        {
            // Add the generated code attribute.
            var code = AddGeneratedCodeAttribute(generatedSyntax);

            // Reference everything which can be referenced.
            var assemblies =
                System.AppDomain.CurrentDomain.GetAssemblies()
                    .Where(asm => !asm.IsDynamic && !string.IsNullOrWhiteSpace(asm.Location))
                    .Select(asm => MetadataReference.CreateFromFile(asm.Location))
                    .Cast<MetadataReference>()
                    .ToArray();
            var logger = LogManager.GetLogger("CodeGenerator");

            // Generate the code.
            var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary);

            string source = null;
            if (logger.IsVerbose3)
            {
                source = GenerateSourceCode(code);

                // Compile the code and load the generated assembly.
                logger.LogWithoutBulkingAndTruncating(
                    Severity.Verbose3,
                    ErrorCode.CodeGenSourceGenerated,
                    "Generating assembly {0} with source:\n{1}",
                    assemblyName,
                    source);
            }
            
            var compilation =
                CSharpCompilation.Create(assemblyName)
                    .AddSyntaxTrees(code.SyntaxTree)
                    .AddReferences(assemblies)
                    .WithOptions(options);

            var outputStream = new MemoryStream();
            var symbolStream = emitDebugSymbols ? new MemoryStream() : null;
            try
            {
                var compilationResult = compilation.Emit(outputStream, symbolStream);
                if (!compilationResult.Success)
                {
                    source = source ?? GenerateSourceCode(code);
                    var errors = string.Join("\n", compilationResult.Diagnostics.Select(_ => _.ToString()));
                    logger.Warn(
                        ErrorCode.CodeGenCompilationFailed,
                        "Compilation of assembly {0} failed with errors:\n{1}\nGenerated Source Code:\n{2}",
                        assemblyName,
                        errors,
                        source);
                    throw new CodeGenerationException(errors);
                }

                logger.Verbose(
                    ErrorCode.CodeGenCompilationSucceeded,
                    "Compilation of assembly {0} succeeded.",
                    assemblyName);
                return new GeneratedAssembly
                {
                    RawBytes = outputStream.ToArray(),
                    DebugSymbolRawBytes = symbolStream?.ToArray()
                };
            }
            finally
            {
                outputStream.Dispose();
                symbolStream?.Dispose();
            }
        }

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
                                SF.AttributeArgument("Orleans-CodeGenerator".GetLiteralExpression()),
                                SF.AttributeArgument(RuntimeVersion.FileVersion.GetLiteralExpression())))
                    .WithTarget(SF.AttributeTargetSpecifier(SF.Token(SyntaxKind.AssemblyKeyword)));
            return generatedSyntax.Syntax.AddAttributeLists(generatedCodeAttribute, codeGenTargetAttributes);
        }

        internal static AttributeSyntax GetGeneratedCodeAttributeSyntax()
        {
            return
                SF.Attribute(typeof(GeneratedCodeAttribute).GetNameSyntax())
                    .AddArgumentListArguments(
                        SF.AttributeArgument(CodeGeneratorName.GetLiteralExpression()),
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
