/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

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

    using GrainInterfaceData = Orleans.CodeGeneration.GrainInterfaceData;
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
        /// <returns>
        /// The raw assembly.
        /// </returns>
        /// <exception cref="CodeGenerationException">
        /// An error occurred generating code.
        /// </exception>
        public static byte[] CompileAssembly(GeneratedSyntax generatedSyntax, string assemblyName)
        {
            // Add the generated code attribute.
            var code = AddGeneratedCodeAttribute(generatedSyntax);

            // Reference everything which can be referenced.
            var assemblies =
                AppDomain.CurrentDomain.GetAssemblies()
                    .Where(asm => !asm.IsDynamic && !string.IsNullOrWhiteSpace(asm.Location))
                    .Select(asm => MetadataReference.CreateFromFile(asm.Location))
                    .Cast<MetadataReference>()
                    .ToArray();
            var logger = TraceLogger.GetLogger("CodeGenerator");

            // Generate the code.
            var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary);

            string source = null;
            if (logger.IsVerbose3)
            {
                source = GenerateSourceCode(code);

                // Compile the code and load the generated assembly.
                if (logger.IsVerbose3)
                {
                    logger.LogWithoutBulkingAndTruncating(
                        Logger.Severity.Verbose3,
                        ErrorCode.CodeGenSourceGenerated,
                        "Generating assembly {0} with source:\n{1}",
                        assemblyName,
                        source);
                }
            }
            
            var compilation =
                CSharpCompilation.Create(assemblyName)
                    .AddSyntaxTrees(code.SyntaxTree)
                    .AddReferences(assemblies)
                    .WithOptions(options);
            using (var stream = new MemoryStream())
            {
                var compilationResult = compilation.Emit(stream);
                if (!compilationResult.Success)
                {
                    source = source ?? GenerateSourceCode(code);
                    var errors = string.Join("\n", compilationResult.Diagnostics.Select(_ => _.ToString()));
                    logger.Warn(ErrorCode.CodeGenCompilationFailed, "Compilation of assembly {0} failed with errors:\n{1}", assemblyName, errors, source);
                    throw new CodeGenerationException(errors);
                }
                
                logger.Verbose(ErrorCode.CodeGenCompilationSucceeded, "Compilation of assembly {0} succeeded.", assemblyName);
                return stream.ToArray();
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
        /// Get types which have corresponding generated classes marked with the provided marker attributes.
        /// </summary>
        /// <returns>Types which have corresponding generated classes marked with any of the provided marker attributes.</returns>
        internal static HashSet<Type> GetTypesWithImplementations(params Type[] markerAttributes)
        {
            // Get assemblies which contain generated code.
            var all =
                AppDomain.CurrentDomain.GetAssemblies()
                    .Where(_ => _.GetCustomAttribute<GeneratedCodeAttribute>() != null)
                    .SelectMany(_ => _.DefinedTypes);

            // Get all generated types in each assembly.
            var attributes = all.SelectMany(_ => _.GetCustomAttributes()).OfType<GeneratedAttribute>();
            var results = new HashSet<Type>();
            foreach (var attribute in attributes)
            {
                if (attribute.GrainType != null)
                {
                    results.Add(attribute.GrainType);
                }
                else if (!string.IsNullOrWhiteSpace(attribute.ForGrainType))
                {
                    results.Add(Type.GetType(attribute.ForGrainType));
                }
            }

            return results;
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
            IdentifierNameSyntax methodIdArgument,
            Func<MethodInfo, StatementSyntax[]> generateMethodHandler)
        {
            var interfaces = GrainInterfaceData.GetRemoteInterfaces(grainType);
            interfaces[GrainInterfaceData.GetGrainInterfaceId(grainType)] = grainType;

            // Switch on interface id.
            var interfaceCases = new List<SwitchSectionSyntax>();
            foreach (var @interface in interfaces)
            {
                var interfaceType = @interface.Value;
                var interfaceId = @interface.Key;
                var methods = GrainInterfaceData.GetMethods(interfaceType);

                var methodCases = new List<SwitchSectionSyntax>();

                // Switch on method id.
                foreach (var method in methods)
                {
                    // Generate switch case.
                    var methodId = GrainInterfaceData.ComputeMethodId(method);
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
