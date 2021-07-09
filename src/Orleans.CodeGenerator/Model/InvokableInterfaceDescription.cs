using Orleans.CodeGenerator.SyntaxGeneration;
using Microsoft.CodeAnalysis;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;

namespace Orleans.CodeGenerator
{
    internal class InvokableInterfaceDescription
    {
        public InvokableInterfaceDescription(
            CodeGenerator generator,
            SemanticModel semanticModel,
            INamedTypeSymbol interfaceType,
            string name,
            INamedTypeSymbol proxyBaseType,
            bool isExtension,
            Dictionary<INamedTypeSymbol, INamedTypeSymbol> invokableBaseTypes)
        {
            ValidateBaseClass(generator.LibraryTypes, proxyBaseType);
            CodeGenerator = generator;
            SemanticModel = semanticModel;
            InterfaceType = interfaceType;
            ProxyBaseType = proxyBaseType;
            IsExtension = isExtension;
            Name = name;

            // If the name is a user-defined name which specified a generic arity, strip the arity backtick now
            if (Name.Contains("`"))
            {
                Name = Name.Replace('`', '_');
            }

            GeneratedNamespace = InterfaceType.GetNamespaceAndNesting() switch
            {
                { Length: > 0 } ns => $"{CodeGenerator.CodeGeneratorName}.{ns}",
                _ => CodeGenerator.CodeGeneratorName
            };

            var names = new HashSet<string>(StringComparer.Ordinal);
            TypeParameters = new List<(string Name, ITypeParameterSymbol Parameter)>();

            foreach (var tp in interfaceType.GetAllTypeParameters())
            {
                var tpName = GetTypeParameterName(names, tp);
                TypeParameters.Add((tpName, tp));
            }

            InvokableBaseTypes = invokableBaseTypes;
            Methods = GetMethods(interfaceType).ToList();

            static string GetTypeParameterName(HashSet<string> names, ITypeParameterSymbol tp)
            {
                var count = 0;
                var result = tp.Name;
                while (names.Contains(result))
                {
                    result = $"{tp.Name}_{++count}";
                }

                names.Add(result);
                return result;
            }
        }

        public CodeGenerator CodeGenerator { get; }

        private IEnumerable<MethodDescription> GetMethods(INamedTypeSymbol symbol)
        {
#pragma warning disable RS1024 // Compare symbols correctly
            var methods = new Dictionary<IMethodSymbol, bool>(MethodSignatureComparer.Default);
#pragma warning restore RS1024 // Compare symbols correctly
            foreach (var iface in GetAllInterfaces(symbol))
            {
                foreach (var method in iface.GetDeclaredInstanceMembers<IMethodSymbol>())
                {
                    if (methods.TryGetValue(method, out var description))
                    {
                        methods[method] = true;
                        continue;
                    }

                    methods.Add(method, false);
                }
            }

            var idCounter = 1;
            foreach (var pair in methods.OrderBy(kv => kv.Key, MethodSignatureComparer.Default))
            {
                var method = pair.Key;
                var id = CodeGenerator.GetId(method) ?? idCounter;
                if (id >= idCounter)
                {
                    idCounter = id + 1;
                }

                yield return new MethodDescription(this, method, id.ToString(CultureInfo.InvariantCulture), hasCollision: pair.Value);
            }

            IEnumerable<INamedTypeSymbol> GetAllInterfaces(INamedTypeSymbol s)
            {
                if (s.TypeKind == TypeKind.Interface)
                {
                    yield return s;
                }

                foreach (var i in s.AllInterfaces)
                {
                    yield return i;
                }
            }
        }

        public string Name { get; }
        public INamedTypeSymbol InterfaceType { get; }
        public List<MethodDescription> Methods { get; }
        public INamedTypeSymbol ProxyBaseType { get; }
        public bool IsExtension { get; }
        public SemanticModel SemanticModel { get; }
        public string GeneratedNamespace { get; }
        public List<(string Name, ITypeParameterSymbol Parameter)> TypeParameters { get; }
        public Dictionary<INamedTypeSymbol, INamedTypeSymbol> InvokableBaseTypes { get; }

        private static void ValidateBaseClass(LibraryTypes l, INamedTypeSymbol baseClass)
        {
            ValidateGenericInvokeAsync(l, baseClass);
            ValidateNonGenericInvokeAsync(l, baseClass);

            static void ValidateGenericInvokeAsync(LibraryTypes l, INamedTypeSymbol baseClass)
            {
                var found = false;
                string complaint = null;
                ISymbol complaintMember = null;
                foreach (var member in baseClass.GetMembers("InvokeAsync"))
                {
                    if (member is not IMethodSymbol method)
                    {
                        complaintMember = member;
                        complaint = "not a method";
                        continue;
                    }

                    if (method.TypeParameters.Length != 1)
                    {
                        complaintMember = member;
                        complaint = "incorrect number of type parameters (expected one type parameter)";
                        continue;
                    }

                    if (method.Parameters.Length != 1)
                    {
                        complaintMember = member;
                        complaint = $"missing parameter (expected a parameter of type {l.IInvokable.ToDisplayString()})";
                        continue;
                    }

                    if (!SymbolEqualityComparer.Default.Equals(method.Parameters[0].Type, l.IInvokable))
                    {
                        complaintMember = member;
                        complaint = $"incorrect parameter type (found {method.Parameters[0].Type}, expected {l.IInvokable})";
                        continue;
                    }

                    var expectedReturnType = l.ValueTask_1.Construct(method.TypeParameters[0]);
                    if (!SymbolEqualityComparer.Default.Equals(method.ReturnType, expectedReturnType))
                    {
                        complaintMember = member;
                        complaint = $"incorrect return type (found: {method.ReturnType.ToDisplayString()}, expected {expectedReturnType.ToDisplayString()})";
                        continue;
                    }

                    found = true;
                }

                if (!found)
                {
                    var notFoundMessage = $"Proxy base class {baseClass} does not contain a definition for ValueTask<T> InvokeAsync<T>(IInvokable)";
                    if (complaint is { Length: > 0 })
                    {
                        throw new InvalidOperationException($"{notFoundMessage}. Complaint: {complaint} for symbol: {complaintMember.ToDisplayString()}");
                    }

                    throw new InvalidOperationException(notFoundMessage);
                }
            }
            
            static void ValidateNonGenericInvokeAsync(LibraryTypes l, INamedTypeSymbol baseClass)
            {
                var found = false;
                string complaint = null;
                ISymbol complaintMember = null;
                foreach (var member in baseClass.GetMembers("InvokeAsync"))
                {
                    if (member is not IMethodSymbol method)
                    {
                        complaintMember = member;
                        complaint = "not a method";
                        continue;
                    }

                    if (method.TypeParameters.Length != 0)
                    {
                        complaintMember = member;
                        complaint = "incorrect number of type parameters (expected zero)";
                        continue;
                    }

                    if (method.Parameters.Length != 1)
                    {
                        complaintMember = member;
                        complaint = $"missing parameter (expected a parameter of type {l.IInvokable.ToDisplayString()})";
                        continue;
                    }

                    if (!SymbolEqualityComparer.Default.Equals(method.Parameters[0].Type, l.IInvokable))
                    {
                        complaintMember = member;
                        complaint = $"incorrect parameter type (found {method.Parameters[0].Type}, expected {l.IInvokable})";
                        continue;
                    }

                    if (!SymbolEqualityComparer.Default.Equals(method.ReturnType, l.ValueTask))
                    {
                        complaintMember = member;
                        complaint = $"incorrect return type (found: {method.ReturnType.ToDisplayString()}, expected {l.ValueTask.ToDisplayString()})";
                        continue;
                    }

                    found = true;
                }

                if (!found)
                {
                    var notFoundMessage = $"Proxy base class {baseClass} does not contain a definition for ValueTask InvokeAsync(IInvokable)";
                    if (complaint is { Length: > 0 })
                    {
                        throw new InvalidOperationException($"{notFoundMessage}. Complaint: {complaint} for symbol: {complaintMember.ToDisplayString()}");
                    }

                    throw new InvalidOperationException(notFoundMessage);
                }
            }
        }

        private sealed class MethodSignatureComparer : IEqualityComparer<IMethodSymbol>, IComparer<IMethodSymbol>
        {
            public static MethodSignatureComparer Default { get; } = new();

            private MethodSignatureComparer()
            {
            }

            public bool Equals(IMethodSymbol x, IMethodSymbol y)
            {
                if (!string.Equals(x.Name, y.Name, StringComparison.Ordinal))
                {
                    return false;
                }

                if (x.TypeArguments.Length != y.TypeArguments.Length)
                {
                    return false;
                }

                for (var i = 0; i < x.TypeArguments.Length; i++)
                {
                    if (!SymbolEqualityComparer.Default.Equals(x.TypeArguments[i], y.TypeArguments[i]))
                    {
                        return false;
                    }
                }

                if (x.Parameters.Length != y.Parameters.Length)
                {
                    return false;
                }

                for (var i = 0; i < x.Parameters.Length; i++)
                {
                    if (!SymbolEqualityComparer.Default.Equals(x.Parameters[i].Type, y.Parameters[i].Type))
                    {
                        return false;
                    }
                }

                return true;
            }

            public int GetHashCode(IMethodSymbol obj)
            {
                int hashCode = -499943048;
                hashCode = hashCode * -1521134295 + StringComparer.Ordinal.GetHashCode(obj.Name);

                foreach (var arg in obj.TypeArguments)
                {
                    hashCode = hashCode * -1521134295 + SymbolEqualityComparer.Default.GetHashCode(arg);
                }

                foreach (var parameter in obj.Parameters)
                {
                    hashCode = hashCode * -1521134295 + SymbolEqualityComparer.Default.GetHashCode(parameter.Type);
                }

                return hashCode;
            }

            public int Compare(IMethodSymbol x, IMethodSymbol y)
            {
                var result = StringComparer.Ordinal.Compare(x.Name, y.Name);
                if (result != 0)
                {
                    return result;
                }

                result = x.TypeArguments.Length.CompareTo(y.TypeArguments.Length);
                if (result != 0)
                {
                    return result;
                }

                for (var i = 0; i < x.TypeArguments.Length; i++)
                {
                    var xh = SymbolEqualityComparer.Default.GetHashCode(x.TypeArguments[i]);
                    var yh = SymbolEqualityComparer.Default.GetHashCode(y.TypeArguments[i]);
                    result = xh.CompareTo(yh);
                    if (result != 0)
                    {
                        return result;
                    }
                }

                result = x.Parameters.Length.CompareTo(y.Parameters.Length);
                if (result != 0)
                {
                    return result;
                }

                for (var i = 0; i < x.Parameters.Length; i++)
                {
                    var xh = SymbolEqualityComparer.Default.GetHashCode(x.Parameters[i].Type);
                    var yh = SymbolEqualityComparer.Default.GetHashCode(y.Parameters[i].Type);
                    result = xh.CompareTo(yh);
                    if (result != 0)
                    {
                        return result;
                    }
                }

                return 0;
            }
        }
    }
}