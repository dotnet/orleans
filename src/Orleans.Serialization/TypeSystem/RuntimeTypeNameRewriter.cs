#nullable enable

using System;

namespace Orleans.Serialization.TypeSystem;

/// <summary>
/// Rewrites <see cref="TypeSpec"/> graphs.
/// </summary>
internal static class RuntimeTypeNameRewriter
{
    /// <summary>
    /// Signature for a delegate which rewrites a <see cref="QualifiedType"/>.
    /// </summary>
    /// <param name="input">The input.</param>
    /// <param name="state">The state provided to the rewriter method.</param>
    /// <returns>The rewritten qualified type.</returns>
    public delegate QualifiedType Rewriter<TState>(in QualifiedType input, ref TState state);

    /// <summary>
    /// Signature for a delegate which resolves a compound type alias.
    /// </summary>
    /// <param name="input">The input.</param>
    /// <param name="state">The state provided to the resolve method.</param>
    /// <returns>The resolved type type.</returns>
    public delegate TypeSpec CompoundAliasResolver<TState>(TupleTypeSpec input, ref TState state);

    /// <summary>
    /// Rewrites a <see cref="TypeSpec"/> using the provided rewriter delegate.
    /// </summary>
    public static TypeSpec Rewrite<TState>(TypeSpec input, Rewriter<TState> rewriter, ref TState state)
    {
        var instance = new TypeRewriter<TState>(rewriter, null, ref state);
        var result = instance.Rewrite(input);
        state = instance._userState;
        return result;
    }

    /// <summary>
    /// Rewrites a <see cref="TypeSpec"/> using the provided rewriter delegate.
    /// </summary>
    public static TypeSpec Rewrite<TState>(TypeSpec input, Rewriter<TState> rewriter, CompoundAliasResolver<TState>? compoundAliasRewriter, ref TState state)
    {
        var instance = new TypeRewriter<TState>(rewriter, compoundAliasRewriter, ref state);
        var result = instance.Rewrite(input);
        state = instance._userState;
        return result;
    }

    private struct TypeRewriter<TState>
    {
        private readonly Rewriter<TState> _nameRewriter { get; }
        private readonly CompoundAliasResolver<TState>? _compoundTypeRewriter { get; }
        public TState _userState;

        public TypeRewriter(Rewriter<TState> nameRewriter, CompoundAliasResolver<TState>? compoundTypeRewriter, ref TState initialUserState)
        {
            _nameRewriter = nameRewriter;
            _compoundTypeRewriter = compoundTypeRewriter;
            _userState = initialUserState;
        }

        public TypeSpec Rewrite(TypeSpec input)
        {
            var result = ApplyInner(input, null);
            if (result.Assembly is not null)
            {
                // If the rewriter bubbled up an assembly, add it here. 
                return new AssemblyQualifiedTypeSpec(result.Type, result.Assembly);
            }

            return result.Type;
        }

        private (TypeSpec Type, string? Assembly) ApplyInner(TypeSpec input, string? assemblyName) =>
        // A type's assembly is passed downwards through the graph, and modifications to the assembly (from the user-provided delegate) flow upwards.
        input switch
        {
            ConstructedGenericTypeSpec type => HandleGeneric(type, assemblyName),
            NamedTypeSpec type => HandleNamedType(type, assemblyName),
            AssemblyQualifiedTypeSpec type => HandleAssembly(type, assemblyName),
            ArrayTypeSpec type => HandleArray(type, assemblyName),
            PointerTypeSpec type => HandlePointer(type, assemblyName),
            ReferenceTypeSpec type => HandleReference(type, assemblyName),
            TupleTypeSpec type => HandleCompoundType(type, assemblyName),
            LiteralTypeSpec type => (type, assemblyName),
            null => throw new ArgumentNullException(nameof(input)),
            _ => throw new NotSupportedException($"Argument of type {input.GetType()} is nut supported"),
        };

        private (TypeSpec Type, string? Assembly) HandleGeneric(ConstructedGenericTypeSpec type, string? assemblyName)
        {
            var (unconstructed, replacementAssembly) = ApplyInner(type.UnconstructedType, assemblyName);
            if (unconstructed is AssemblyQualifiedTypeSpec assemblyQualified)
            {
                unconstructed = assemblyQualified.Type;
                replacementAssembly = assemblyQualified.Assembly;
            }

            var newArguments = new TypeSpec[type.Arguments.Length];
            var didChange = false;
            for (var i = 0; i < type.Arguments.Length; i++)
            {
                // Generic type parameters do not inherit the assembly of the generic type.
                var args = ApplyInner(type.Arguments[i], null);

                if (args.Assembly is not null)
                {
                    newArguments[i] = new AssemblyQualifiedTypeSpec(args.Type, args.Assembly);
                }
                else
                {
                    newArguments[i] = args.Type;
                }

                didChange |= !ReferenceEquals(newArguments[i], type.Arguments[i]);
            }

            if (ReferenceEquals(type.UnconstructedType, unconstructed) && !didChange)
            {
                return (type, replacementAssembly);
            }

            return (new ConstructedGenericTypeSpec(unconstructed, newArguments.Length, newArguments), replacementAssembly);
        }

        private (TypeSpec Type, string? Assembly) HandleNamedType(NamedTypeSpec type, string? assemblyName)
        {
            var nsQualified = type.GetNamespaceQualifiedName();
            var replacementName = _nameRewriter(new QualifiedType(assembly: assemblyName, type: nsQualified), ref _userState);

            if (!string.Equals(nsQualified, replacementName.Type, StringComparison.Ordinal))
            {
                // Change the type name and potentially the assembly.
                var resultType = RuntimeTypeNameParser.Parse(replacementName.Type);
                if (!(resultType is NamedTypeSpec))
                {
                    throw new InvalidOperationException($"Replacement type name, \"{replacementName.Type}\", can not deviate from the original type structure of the input, \"{nsQualified}\"");
                }

                return (resultType, replacementName.Assembly);
            }
            else if (!string.Equals(assemblyName, replacementName.Assembly, StringComparison.Ordinal))
            {
                // Only change the assembly;
                return (type, replacementName.Assembly);
            }
            else if (type.ContainingType is not null)
            {
                // Give the user an opportunity to change the parent, including the assembly.
                var replacementParent = ApplyInner(type.ContainingType, assemblyName);
                if (ReferenceEquals(replacementParent.Type, type.ContainingType))
                {
                    // No change to the type.
                    return (type, replacementParent.Assembly);
                }

                // The parent type changed.
                var typedReplacement = (NamedTypeSpec)replacementParent.Type;
                return (new NamedTypeSpec(typedReplacement, type.Name, type.Arity), replacementParent.Assembly);
            }
            else
            {
                return (type, replacementName.Assembly);
            }
        }

        private (TypeSpec Type, string? Assembly) HandleAssembly(AssemblyQualifiedTypeSpec type, string? assemblyName)
        {
            var replacement = ApplyInner(type.Type, type.Assembly);

            // Assembly name changes never bubble up past the assembly qualifier node.
            if (string.IsNullOrWhiteSpace(replacement.Assembly))
            {
                // Remove the assembly qualification
                return (replacement.Type, assemblyName);
            }
            else if (!string.Equals(replacement.Assembly, type.Assembly) || !ReferenceEquals(replacement.Type, type.Type))
            {
                // Update the assembly or the type.
                return (new AssemblyQualifiedTypeSpec(replacement.Type, replacement.Assembly), assemblyName);
            }

            // No update.
            return (type, assemblyName);
        }

        private (TypeSpec Type, string? Assembly) HandleArray(ArrayTypeSpec type, string? assemblyName)
        {
            var element = ApplyInner(type.ElementType, assemblyName);
            if (ReferenceEquals(element.Type, type.ElementType))
            {
                return (type, element.Assembly);
            }

            return (new ArrayTypeSpec(element.Type, type.Dimensions), element.Assembly);
        }

        private (TypeSpec Type, string? Assembly) HandleReference(ReferenceTypeSpec type, string? assemblyName)
        {
            var element = ApplyInner(type.ElementType, assemblyName);
            if (ReferenceEquals(element.Type, type.ElementType))
            {
                return (type, element.Assembly);
            }

            return (new ReferenceTypeSpec(element.Type), element.Assembly);
        }

        private (TypeSpec Type, string? Assembly) HandlePointer(PointerTypeSpec type, string? assemblyName)
        {
            var element = ApplyInner(type.ElementType, assemblyName);
            if (ReferenceEquals(element.Type, type.ElementType))
            {
                return (type, element.Assembly);
            }

            return (new PointerTypeSpec(element.Type), element.Assembly);
        }

        private (TypeSpec Type, string? Assembly) HandleCompoundType(TupleTypeSpec type, string? assemblyName)
        {
            var elements = new TypeSpec[type.Elements.Length];
            for (var i = 0; i < type.Elements.Length; i++)
            {
                (elements[i], _) = ApplyInner(type.Elements[i], assemblyName);
            }

            // Resolve the compound type alias after first trying to resolve each of the individual elements.
            var replacementTypeSpec = new TupleTypeSpec(elements, type.Arity);
            if (_compoundTypeRewriter is { } rewriter)
            {
                var resolved = rewriter(replacementTypeSpec, ref _userState);
                if (resolved is TupleTypeSpec)
                {
                    throw new InvalidOperationException($"Compound type alias resolver resolved {type} into {resolved} which is also an alias type.");
                }

                // Give the rewriter a chance to rewrite the resolved name.
                return ApplyInner(resolved, assemblyName); 
            }

            return (replacementTypeSpec, assemblyName);
        }
    }
}