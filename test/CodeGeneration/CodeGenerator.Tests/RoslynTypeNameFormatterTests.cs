using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Orleans;
using Orleans.CodeGenerator;
using Orleans.CodeGeneration;
using Orleans.CodeGenerator.Compatibility;
using Orleans.Runtime;
using Orleans.Utilities;
using Xunit;
using Xunit.Abstractions;
using Orleans.CodeGenerator.Model;
using System.Text;
using Orleans.Serialization;

[assembly: System.Reflection.AssemblyCompanyAttribute("Microsoft")]
[assembly: System.Reflection.AssemblyFileVersionAttribute("2.0.0.0")]
[assembly: System.Reflection.AssemblyInformationalVersionAttribute("2.0.0")]
[assembly: System.Reflection.AssemblyProductAttribute("Microsoft Orleans")]
[assembly: System.Reflection.AssemblyTitleAttribute("CodeGenerator.Tests")]
[assembly: System.Reflection.AssemblyVersionAttribute("2.0.0.0")]

namespace CodeGenerator.Tests
{
    /// <summary>
    /// Tests for <see cref="RoslynTypeNameFormatter"/>.
    /// </summary>
    [Trait("Category", "BVT")]
    public class RoslynTypeNameFormatterTests
    {
        private readonly ITestOutputHelper output;

        private static readonly Type[] Types =
        {
            typeof(int),
            typeof(int[]),
            typeof(int**[]),
            typeof(List<>),
            typeof(Dictionary<string, Dictionary<int, bool>>),
            typeof(Dictionary<,>),
            typeof(List<int>),
            typeof(List<int*[]>),
            typeof(List<int*[]>.Enumerator),
            typeof(List<>.Enumerator),
            typeof(Generic<>),
            typeof(Generic<>.Nested),
            typeof(Generic<>.NestedGeneric<>),
            typeof(Generic<>.NestedMultiGeneric<,>),
            typeof(Generic<int>.Nested),
            typeof(Generic<int>.NestedGeneric<bool>),
            typeof(Generic<int>.NestedMultiGeneric<Generic<int>.NestedGeneric<bool>, double>)
        };

        private static readonly Type[] Grains =
        {
            typeof(IMyGenericGrainInterface2<>),
            typeof(IMyGenericGrainInterface2<int>),
            typeof(IMyGrainInterface),
            typeof(IMyGenericGrainInterface<int>),
            typeof(IMyGrainInterfaceWithTypeCodeOverride),
            typeof(MyGrainClass),
            typeof(MyGenericGrainClass<int>),
            typeof(MyGrainClassWithTypeCodeOverride),
            typeof(NotNested.IMyGrainInterface),
            typeof(NotNested.IMyGenericGrainInterface<int>),
            typeof(NotNested.MyGrainClass),
            typeof(NotNested.MyGenericGrainClass<int>),
            typeof(NotNested.IMyGenericGrainInterface<>),
            typeof(NotNested.MyGenericGrainClass<>),
        };

        private readonly CSharpCompilation compilation;

        public RoslynTypeNameFormatterTests(ITestOutputHelper output)
        {
            this.output = output;

            // Read the source code of this file and parse that with Roslyn.
            var testCode = GetSource();

            var metas = new[]
            {
                typeof(int).Assembly,
                typeof(IGrain).Assembly,
                typeof(Attribute).Assembly,
                typeof(System.Net.IPAddress).Assembly,
                typeof(ExcludeFromCodeCoverageAttribute).Assembly,
            }.Select(a => MetadataReference.CreateFromFile(a.Location, MetadataReferenceProperties.Assembly));
            var metadataReferences = metas.Concat(GetGlobalReferences()).ToArray();

            var syntaxTrees = new[] { CSharpSyntaxTree.ParseText(testCode, path: "TestProgram.cs") };
            var assemblyName = typeof(RoslynTypeNameFormatterTests).Assembly.GetName().Name;
            var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary).WithMetadataImportOptions(MetadataImportOptions.All);
            this.compilation = CSharpCompilation.Create(assemblyName, syntaxTrees, metadataReferences, options);

            IEnumerable<MetadataReference> GetGlobalReferences()
            {
                // The location of the .NET assemblies
                var assemblyPath = Path.GetDirectoryName(typeof(object).Assembly.Location);
                return new List<MetadataReference>
                {
                    MetadataReference.CreateFromFile(Path.Combine(assemblyPath, "netstandard.dll")),
                    MetadataReference.CreateFromFile(Path.Combine(assemblyPath, "mscorlib.dll")),
                    MetadataReference.CreateFromFile(Path.Combine(assemblyPath, "System.dll")),
                    MetadataReference.CreateFromFile(Path.Combine(assemblyPath, "System.Core.dll")),
                    MetadataReference.CreateFromFile(Path.Combine(assemblyPath, "System.Runtime.dll")),
                    MetadataReference.CreateFromFile(Path.Combine(assemblyPath, "System.Runtime.Serialization.Formatters.dll"))
                };
            }
        }

        private string GetSource()
        {
            var type = typeof(RoslynTypeNameFormatterTests);
            using (var stream = type.Assembly.GetManifestResourceStream($"{type.Namespace}.{type.Name}.cs"))
            using (StreamReader reader = new StreamReader(stream))
            {
                return reader.ReadToEnd();
            }
        }

        /// <summary>
        /// Tests that various strings formatted with <see cref="RoslynTypeNameFormatter"/> match their corresponding <see cref="Type.FullName"/> values.
        /// </summary>
        [Fact]
        public void FullNameMatchesClr()
        {
            foreach (var (type, symbol) in GetTypeSymbolPairs(nameof(Types)))
            {
                this.output.WriteLine($"Type: {RuntimeTypeNameFormatter.Format(type)}");
                var expected = type.FullName;
                var actual = RoslynTypeNameFormatter.Format(symbol, RoslynTypeNameFormatter.Style.FullName);
                this.output.WriteLine($"Expected FullName: {expected}\nActual FullName:   {actual}");
                Assert.Equal(expected, actual);
            }
        }

        [Fact]
        public void TypeKeyMatchesRuntimeTypeKey()
        {
            foreach (var (type, symbol) in GetTypeSymbolPairs(nameof(Types)))
            {
                var expectedTypeKey = TypeUtilities.OrleansTypeKeyString(type);
                var actualTypeKey = OrleansLegacyCompat.OrleansTypeKeyString(symbol);
                this.output.WriteLine($"Type: {RuntimeTypeNameFormatter.Format(type)}");
                Assert.Equal(expectedTypeKey, actualTypeKey);
            }
        }

        /// <summary>
        /// Tests that the ITypeSymbol id generation algorithm generates ids which match the Type-based generator.
        /// </summary>
        [Fact]
        public void TypeCodesMatch()
        {
            var wellKnownTypes = new WellKnownTypes(this.compilation);
            foreach (var (type, symbol) in GetTypeSymbolPairs(nameof(Grains)))
            {
                this.output.WriteLine($"Type: {RuntimeTypeNameFormatter.Format(type)}");

                {
                    // First check Type.FullName matches.
                    var expected = type.FullName;
                    var actual = RoslynTypeNameFormatter.Format(symbol, RoslynTypeNameFormatter.Style.FullName);
                    this.output.WriteLine($"Expected FullName: {expected}\nActual FullName:   {actual}");
                    Assert.Equal(expected, actual);
                }
                {
                    var expected = TypeUtils.GetTemplatedName(
                        TypeUtils.GetFullName(type),
                        type,
                        type.GetGenericArguments(),
                        t => false);
                    var named = Assert.IsAssignableFrom<INamedTypeSymbol>(symbol);
                    var actual = OrleansLegacyCompat.FormatTypeForIdComputation(named);
                    this.output.WriteLine($"Expected format: {expected}\nActual format:   {actual}");
                    Assert.Equal(expected, actual);
                }
                {
                    var expected = GrainInterfaceUtils.GetGrainInterfaceId(type);
                    var named = Assert.IsAssignableFrom<INamedTypeSymbol>(symbol);
                    var actual = wellKnownTypes.GetTypeId(named);
                    this.output.WriteLine($"Expected Id: 0x{expected:X}\nActual Id:   0x{actual:X}");
                    Assert.Equal(expected, actual);
                }
            }
        }

        /// <summary>
        /// Tests that the IMethodSymbol id generation algorithm generates ids which match the MethodInfo-based generator.
        /// </summary>
        [Fact]
        public void MethodIdsMatch()
        {
            var wellKnownTypes = new WellKnownTypes(this.compilation);
            foreach (var (type, typeSymbol) in GetTypeSymbolPairs(nameof(Grains)))
            {
                this.output.WriteLine($"Type: {RuntimeTypeNameFormatter.Format(type)}");

                var methods = type.GetMethods();
                var methodSymbols = methods.Select(m => typeSymbol.GetMembers(m.Name).SingleOrDefault()).OfType<IMethodSymbol>();

                foreach (var (method, methodSymbol) in methods.Zip(methodSymbols, ValueTuple.Create))
                {
                    this.output.WriteLine($"IMethodSymbol: {methodSymbol}, MethodInfo: {method}");
                    Assert.NotNull(methodSymbol);

                    {
                        var expected = GrainInterfaceUtils.FormatMethodForIdComputation(method);
                        var actual = OrleansLegacyCompat.FormatMethodForMethodIdComputation(methodSymbol);
                        this.output.WriteLine($"Expected format: {expected}\nActual format:   {actual}");
                        Assert.Equal(expected, actual);
                    }

                    {
                        var expected = GrainInterfaceUtils.ComputeMethodId(method);
                        var actual = wellKnownTypes.GetMethodId(methodSymbol);
                        this.output.WriteLine($"Expected Id: 0x{expected:X}\nActual Id:   0x{actual:X}");
                        Assert.Equal(expected, actual);
                    }
                }
            }
        }

        private IEnumerable<(Type, ITypeSymbol)> GetTypeSymbolPairs(string fieldName)
        {
            var typesMember = compilation.Assembly.GlobalNamespace
                .GetMembers("CodeGenerator")
                .First()
                .GetMembers("Tests")
                .Cast<INamespaceOrTypeSymbol>()
                .First()
                .GetTypeMembers("RoslynTypeNameFormatterTests")
                .First()
                .GetMembers(fieldName)
                .First();
            var declaratorSyntax = Assert.IsType<VariableDeclaratorSyntax>(typesMember.DeclaringSyntaxReferences.First().GetSyntax());
            var creationExpressionSyntax = Assert.IsType<InitializerExpressionSyntax>(declaratorSyntax.Initializer.Value);
            var expressions = creationExpressionSyntax.Expressions;
            var typeSymbols = new List<ITypeSymbol>();
            var model = compilation.GetSemanticModel(declaratorSyntax.SyntaxTree);

            foreach (var expr in expressions.ToList().OfType<TypeOfExpressionSyntax>())
            {
                var info = model.GetTypeInfo(expr.Type);
                Assert.NotNull(info.Type);
                typeSymbols.Add(info.Type);
            }

            var types = (Type[])this.GetType().GetField(fieldName, BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            var pairs = types.Zip(typeSymbols, ValueTuple.Create);
            return pairs;
        }

        public interface IMyGrainInterface : IGrainWithGuidKey
        {
            Task One(int a, int b, int c);
            Task<int> Two();
        }

        public class MyGrainClass : Grain, IMyGrainInterface
        {
            public Task One(int a, int b, int c) => throw new NotImplementedException();
            public Task<int> Two() => throw new NotImplementedException();
        }

        public interface IMyGenericGrainInterface<T> : IGrainWithGuidKey
        {
            Task One(T a, int b, int c);
            Task<T> Two();
        }

        public interface IMyGenericGrainInterface2<in T> : IGrainWithGuidKey
        {
            Task One(T a, int b, int c);
        }

        public class MyGenericGrainClass<T> : Grain, IMyGenericGrainInterface<T>
        {
            public Task One(T a, int b, int c) => throw new NotImplementedException();
            public Task<T> Two() => throw new NotImplementedException();
        }

        [TypeCodeOverride(1)]
        public interface IMyGrainInterfaceWithTypeCodeOverride : IGrainWithGuidKey
        {
            [MethodId(1)]
            Task One(int a, int b, int c);

            [MethodId(2)]
            Task<int> Two();
        }

        [TypeCodeOverride(2)]
        public class MyGrainClassWithTypeCodeOverride : Grain, IMyGrainInterfaceWithTypeCodeOverride
        {
            [MethodId(12234)]
            public Task One(int a, int b, int c) => throw new NotImplementedException();

            [MethodId(-41243)]
            public Task<int> Two() => throw new NotImplementedException();
        }
    }

    namespace NotNested
    {
        public interface IMyGrainInterface : IGrainWithGuidKey
        {
            Task One(int a, int b, int c);
            Task<int> Two();
        }

        public class MyGrainClass : Grain, IMyGrainInterface
        {
            public Task One(int a, int b, int c) => throw new NotImplementedException();
            public Task<int> Two() => throw new NotImplementedException();
        }

        public interface IMyGenericGrainInterface<T> : IGrainWithGuidKey
        {
            Task One(T a, int b, int c);
            Task<T> Two(T val);
            Task<TU> Three<TU>(TU val);
        }

        public class MyGenericGrainClass<T> : Grain, IMyGenericGrainInterface<T>
        {
            public Task One(T a, int b, int c) => throw new NotImplementedException();
            public Task<T> Two(T val) => throw new NotImplementedException();
            public Task<TU> Three<TU>(TU val) => throw new NotImplementedException();
        }
    }
}

namespace System
{
    public class Generic<T>
    {
        public class Nested { }
        public class NestedGeneric<TU> { }
        public class NestedMultiGeneric<TU, TV> { }
    }
}