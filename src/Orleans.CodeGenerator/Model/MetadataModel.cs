using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Generic;

namespace Orleans.CodeGenerator
{
    internal class MetadataModel
    {
        public List<ISerializableTypeDescription> SerializableTypes { get; } = new(1024);
        public List<InvokableInterfaceDescription> InvokableInterfaces { get; } = new(1024);
        public List<INamedTypeSymbol> InvokableInterfaceImplementations { get; } = new(1024);
        public Dictionary<MethodDescription, GeneratedInvokerDescription> GeneratedInvokables { get; } = new();
        public List<GeneratedProxyDescription> GeneratedProxies { get; } = new(1024);
        public List<ISerializableTypeDescription> ActivatableTypes { get; } = new(1024);
        public List<INamedTypeSymbol> DetectedSerializers { get; } = new();
        public List<INamedTypeSymbol> DetectedActivators { get; } = new();
        public Dictionary<ISerializableTypeDescription, TypeSyntax> DefaultCopiers { get; } = new();
        public List<INamedTypeSymbol> DetectedCopiers { get; } = new();
        public List<INamedTypeSymbol> DetectedConverters { get; } = new();
        public List<(TypeSyntax Type, string Alias)> TypeAliases { get; } = new(1024);
        public CompoundTypeAliasTree CompoundTypeAliases { get; } = CompoundTypeAliasTree.Create();
        public List<(TypeSyntax Type, uint Id)> WellKnownTypeIds { get; } = new(1024);
        public HashSet<string> ApplicationParts { get; } = new();
    }

    /// <summary>
    /// Represents a compound type aliases as a prefix tree.
    /// </summary>
    internal sealed class CompoundTypeAliasTree
    {
        private Dictionary<object, CompoundTypeAliasTree> _children;

        /// <summary>
        /// Initializes a new instance of the <see cref="CompoundTypeAliasTree"/> class.
        /// </summary>
        private CompoundTypeAliasTree(CompoundTypeAliasComponent key, TypeSyntax value)
        {
            Key = key;
            Value = value;
        }

        /// <summary>
        /// Gets the key for this node.
        /// </summary>
        public CompoundTypeAliasComponent Key { get; }

        /// <summary>
        /// Gets the value for this node.
        /// </summary>
        public TypeSyntax Value { get; private set; }

        /// <summary>
        /// Creates a new tree with a root node which has no key or value.
        /// </summary>
        public static CompoundTypeAliasTree Create() => new(default, default);

        public Dictionary<object, CompoundTypeAliasTree> Children => _children;

        internal CompoundTypeAliasTree GetChildOrDefault(object key)
        {
            TryGetChild(key, out var result);
            return result;
        }

        internal bool TryGetChild(object key, out CompoundTypeAliasTree result)
        {
            if (_children is { } children)
            {
                return children.TryGetValue(key, out result);
            }

            result = default;
            return false;
        }

        public void Add(CompoundTypeAliasComponent[] key, TypeSyntax value)
        {
            Add(key.AsSpan(), value);
        }

        public void Add(ReadOnlySpan<CompoundTypeAliasComponent> keys, TypeSyntax value)
        {
            if (keys.Length == 0)
            {
                throw new InvalidOperationException("No valid key specified.");
            }

            var key = keys[0];
            if (keys.Length == 1)
            {
                AddInternal(key, value);
            }
            else
            {
                var childNode = GetChildOrDefault(key) ?? AddInternal(key);
                childNode.Add(keys.Slice(1), value);
            }
        }

        /// <summary>
        /// Adds a node to the tree.
        /// </summary>
        /// <param name="key">The key for the new node.</param>
        public CompoundTypeAliasTree Add(ITypeSymbol key) => AddInternal(new CompoundTypeAliasComponent(key));

        /// <summary>
        /// Adds a node to the tree.
        /// </summary>
        /// <param name="key">The key for the new node.</param>
        public CompoundTypeAliasTree Add(string key) => AddInternal(new CompoundTypeAliasComponent(key));

        /// <summary>
        /// Adds a node to the tree.
        /// </summary>
        /// <param name="key">The key for the new node.</param>
        /// <param name="value">The value for the new node.</param>
        public CompoundTypeAliasTree Add(string key, TypeSyntax value) => AddInternal(new CompoundTypeAliasComponent(key), value);

        /// <summary>
        /// Adds a node to the tree.
        /// </summary>
        /// <param name="key">The key for the new node.</param>
        /// <param name="value">The value for the new node.</param>
        public CompoundTypeAliasTree Add(ITypeSymbol key, TypeSyntax value) => AddInternal(new CompoundTypeAliasComponent(key), value);

        private CompoundTypeAliasTree AddInternal(CompoundTypeAliasComponent key) => AddInternal(key, default);
        private CompoundTypeAliasTree AddInternal(CompoundTypeAliasComponent key, TypeSyntax value)
        {
            _children ??= new();

            if (_children.TryGetValue(key, out var existing))
            {
                if (value is not null && existing.Value is not null)
                {
                    throw new ArgumentException("A key with this value already exists");
                }

                existing.Value = value;
                return existing;
            }
            else
            {
                return _children[key] = new CompoundTypeAliasTree(key, value);
            }
        }
    }

    internal readonly struct CompoundTypeAliasComponent : IEquatable<CompoundTypeAliasComponent>
    {
        private readonly Either<string, ITypeSymbol> _value;
        public CompoundTypeAliasComponent(string value) => _value = new Either<string, ITypeSymbol>(value);
        public CompoundTypeAliasComponent(ITypeSymbol value) => _value = new Either<string, ITypeSymbol>(value);
        public static CompoundTypeAliasComponent Default => new();
        public bool IsDefault => _value.RawValue is null;
        public bool IsString => _value.IsLeft;
        public string StringValue => _value.LeftValue;
        public bool IsType => _value.IsRight;
        public ITypeSymbol TypeValue => _value.RightValue;
        public object Value => _value.RawValue;

        public bool Equals(CompoundTypeAliasComponent other) => (Value, other.Value) switch
        {
            (null, null) => true,
            (string stringValue, string otherStringValue) => string.Equals(stringValue, otherStringValue),
            (ITypeSymbol typeValue, ITypeSymbol otherTypeValue) => SymbolEqualityComparer.Default.Equals(typeValue, otherTypeValue),
            _ => false,
        };
        public override bool Equals(object obj) => obj is CompoundTypeAliasComponent other && Equals(other);
        public override int GetHashCode() => _value.RawValue switch
        {
            string stringValue => stringValue.GetHashCode(),
            ITypeSymbol type => SymbolEqualityComparer.Default.GetHashCode(type),
            _ => throw new InvalidOperationException($"Unsupported type {_value.RawValue}")
        };

        internal readonly struct EqualityComparer : IEqualityComparer<CompoundTypeAliasComponent>
        {
            public static EqualityComparer Default => default;
            public bool Equals(CompoundTypeAliasComponent x, CompoundTypeAliasComponent y) => x.Equals(y);
            public int GetHashCode(CompoundTypeAliasComponent obj) => obj.GetHashCode();
        }
    }

    internal readonly struct Either<T, U> where T : class where U : class
    {
        private readonly bool _isLeft;
        private readonly object _value;
        public Either(T value)
        {
            _value = value;
            _isLeft = true;
        }

        public Either(U value)
        {
            _value = value;
            _isLeft = false;
        }

        public bool IsLeft => _isLeft;
        public bool IsRight => !IsLeft;
        public T LeftValue => (T)_value;
        public U RightValue => (U)_value;
        public object RawValue => _value;
    }
}