using Microsoft.CodeAnalysis;
using System;
using System.Collections.Generic;

namespace Orleans.CodeGenerator
{
    internal sealed class MethodSignatureComparer : IEqualityComparer<IMethodSymbol>, IComparer<IMethodSymbol>
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