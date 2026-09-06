using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

#if !NET6_0_OR_GREATER
namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.Parameter, Inherited = false)]
    internal sealed class CallerArgumentExpressionAttribute(string parameterName) : Attribute
    {
        public string ParameterName { get; } = parameterName;
    }
}
#endif

namespace Orleans.Serialization.Codecs
{
    internal static class ArgumentNullExceptionPolyfill
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ThrowIfNull(
            [NotNull] object? argument,
            [CallerArgumentExpression(nameof(argument))] string? paramName = null)
        {
            if (argument is null)
            {
                Throw(paramName);
            }
        }

        [DoesNotReturn]
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Throw(string? paramName) => throw new ArgumentNullException(paramName);
    }
}
