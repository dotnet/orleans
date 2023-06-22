namespace Orleans.CodeGenerator.Generators.CompoundAliasGenerators;

using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Orleans.CodeGenerator.SyntaxGeneration;

internal partial class CompoundAliasGenerator
{
    internal class Parser : ParserBase
    {

        private static INamedTypeSymbol _compoundAliasAttribute;

        private static CompoundAliasGeneratorContext _compoundAliasContext;
        private ImmutableArray<(TypeDeclarationSyntax, SemanticModel)> _aliasTypes;

        public Parser(Compilation compilation) : base(compilation)
        {

            _compoundAliasAttribute = Type(Constants.CompoundTypeAliasAttribute);
            _compoundAliasContext = new()
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
            SetContextValuesForCurrentAssembly(token);
            SetContextValuesForDeclaringAssemblies(token);
            return _compoundAliasContext;
        }

        private void SetContextValuesForDeclaringAssemblies(CancellationToken token)
        {
            var declaringAssemblies = GetDeclaringAssemblies();

            if (!declaringAssemblies.Any()) return;

            foreach (var assembly in declaringAssemblies)
            {
                foreach (var typeSymbol in assembly.GetDeclaredTypes())
                {
                    if (GetCompoundTypeAlias(typeSymbol) is CompoundTypeAliasComponent[] compoundTypeAlias)
                        _compoundAliasContext.CompoundTypeAliases.Add(compoundTypeAlias, typeSymbol.ToOpenTypeSyntax());
                }
            }


        }

        IncrementalGeneratorContext SetContextValuesForCurrentAssembly(CancellationToken token)
        {
            foreach (var type in _aliasTypes)
            {
                var symbol = (ITypeSymbol)type.Item2.GetDeclaredSymbol(type.Item1);

                if (GetCompoundTypeAlias(symbol) is CompoundTypeAliasComponent[] compoundTypeAlias)
                    _compoundAliasContext.CompoundTypeAliases.Add(compoundTypeAlias, symbol.ToOpenTypeSyntax());


            }
            return _compoundAliasContext;
        }

        private CompoundTypeAliasComponent[] GetCompoundTypeAlias(ISymbol symbol)
        {
            var attr = symbol.GetAttribute(_compoundAliasAttribute);
            if (attr is null)
            {
                return null;
            }

            var allArgs = attr.ConstructorArguments;
            if (allArgs.Length != 1 || allArgs[0].Values.Length == 0)
            {
                throw new ArgumentException($"Unsupported arguments in attribute [{attr.AttributeClass.Name}({string.Join(", ", allArgs.Select(a => a.ToCSharpString()))})]");
            }

            var args = allArgs[0].Values;
            var result = new CompoundTypeAliasComponent[args.Length];
            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if (arg.IsNull)
                {
                    throw new ArgumentNullException($"Unsupported null argument in attribute [{attr.AttributeClass.Name}({string.Join(", ", allArgs.Select(a => a.ToCSharpString()))})]");
                }

                result[i] = arg.Value switch
                {
                    ITypeSymbol type => new CompoundTypeAliasComponent(type),
                    string str => new CompoundTypeAliasComponent(str),
                    _ => throw new ArgumentException($"Unrecognized argument type for argument {arg.ToCSharpString()} in attribute [{attr.AttributeClass.Name}({string.Join(", ", allArgs.Select(a => a.ToCSharpString()))})]"),
                };
            }

            return result;
        }


    }
}
