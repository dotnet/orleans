using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Orleans.Serialization.Configuration;
using Xunit.Abstractions;

namespace Orleans.CodeGenerator.Tests
{
    public abstract class CodeGeneratorTestBase
    {
        //static readonly PortableExecutableReference[] _frameworkCompilationReferences = new[]
        //{
        //    //Basic.Reference.Assemblies.Net60.mscorlib,
        //    //Basic.Reference.Assemblies.Net60.System,
        //    //Basic.Reference.Assemblies.Net60.SystemRuntime,
        //    //Basic.Reference.Assemblies.Net60.SystemBuffers
        //};
        static readonly PortableExecutableReference[] _frameworkCompilationReferences = Basic.Reference.Assemblies.Net60.All.ToArray();

        static readonly PortableExecutableReference[] _extraCompilationReferences = new[]
        {
            MetadataReference.CreateFromFile(typeof(GenerateSerializerAttribute).Assembly.Location), // Orleans.Serialization.Abstractions
            MetadataReference.CreateFromFile(typeof(ITypeManifestProvider).Assembly.Location) // Orleans.Serialization
        };

        static readonly SyntaxTree _globalUsings = CSharpSyntaxTree.ParseText("""
            global using System;
            global using Orleans;
            """);

        readonly ITestOutputHelper _testOutputHelper;

        protected CodeGeneratorTestBase()
        {
        }

        protected CodeGeneratorTestBase(ITestOutputHelper testOutputHelper)
        {
            _testOutputHelper = testOutputHelper;
        }

        protected Compilation CreateCompilation(string sourceText)
        {
            var compilation = CSharpCompilation.Create("compilation",
                new[] { _globalUsings, CSharpSyntaxTree.ParseText(sourceText) },
                _frameworkCompilationReferences.Union(_extraCompilationReferences),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            var diagnostics = compilation.GetDiagnostics();

            var compilationDiagnostics = compilation.GetDiagnostics();

            if (!compilationDiagnostics.IsEmpty)
            {
                _testOutputHelper?.WriteLine($"Original compilation diagnostics produced:");

                foreach (var diagnostic in compilationDiagnostics)
                {
                    _testOutputHelper?.WriteLine($" > " + diagnostic.ToString());
                }

                if (compilationDiagnostics.Any(x => x.Severity == DiagnosticSeverity.Error))
                {
                    Debug.Fail("Compilation diagnostics produced");
                }
            }

            return compilation;
        }

        protected CompilationUnitSyntax GenerateCodeFrom(string sourceText, CodeGeneratorOptions codeGeneratorOptions = null)
        {
            var compilation = CreateCompilation(sourceText);
            var codeGenerator = new CodeGenerator(compilation, codeGeneratorOptions ?? new CodeGeneratorOptions());

            var generatedCode = codeGenerator.GenerateCode(default);
            if (generatedCode.ContainsDiagnostics)
            {
                var diagnostics = generatedCode.GetDiagnostics();
                foreach (var diagnostic in diagnostics)
                {
                    _testOutputHelper?.WriteLine($" > " + diagnostic.ToString());
                }

                if (diagnostics.Any(x => x.Severity == DiagnosticSeverity.Error))
                {
                    Debug.Fail("Compilation diagnostics produced");
                }
            }

            return generatedCode.NormalizeWhitespace();
        }
    }
}
