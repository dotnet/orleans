namespace Orleans.CodeGenerator.Generators.AliasGenerators;

using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Orleans.CodeGenerator.SyntaxGeneration;

internal partial class AliasGenerator
{
    internal class Parser : ParserBase
    {

        private static INamedTypeSymbol _aliasAttribute;

        private static AliasGeneratorContext _aliasContext;
        private ImmutableArray<(TypeDeclarationSyntax, SemanticModel)> _aliasTypes;

        public Parser(Compilation compilation) : base(compilation)
        {

            _aliasAttribute = Type(Constants.AliasAttribute);
            _aliasContext = new()
            {
                AssemblyName = compilation.AssemblyName,
            };
        }

        public Parser(Compilation compilation, ImmutableArray<(TypeDeclarationSyntax, SemanticModel)> aliasTypes) : this(compilation)
        {
            _aliasTypes = aliasTypes;
        }

        public override IncrementalGeneratorContext Parse(CancellationToken token)
        {
            SetCurrentAssemblyAliasInContext(token);
            SetDeclaringAssembliesAliasInContext(token);
            return _aliasContext;
        }

        private void SetDeclaringAssembliesAliasInContext(CancellationToken token)
        {
            var declaringAssemblies = GetDeclaringAssemblies();

            if (!declaringAssemblies.Any()) return;

            foreach (var assembly in declaringAssemblies)
            {
                foreach (var typeSymbol in assembly.GetDeclaredTypes())
                {
                    if (GetAlias(typeSymbol) is string typeAlias)
                    {
                        _aliasContext.TypeAliases.Add((typeSymbol.ToOpenTypeSyntax(), typeAlias));
                    }
                }
            }


        }

        IncrementalGeneratorContext SetCurrentAssemblyAliasInContext(CancellationToken token)
        {
            foreach (var type in _aliasTypes)
            {
                var symbol = (ITypeSymbol)type.Item2.GetDeclaredSymbol(type.Item1);
                var aliasConstructorValue = GetAlias(symbol);

                _aliasContext.TypeAliases.Add((symbol.ToOpenTypeSyntax(), aliasConstructorValue));
            }
            return _aliasContext;
        }

        private static string GetAlias(ISymbol symbol)
        {
            return (string)symbol.GetAttribute(_aliasAttribute)?.ConstructorArguments.First().Value;
        }
    }
}
