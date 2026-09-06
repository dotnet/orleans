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
#if NET6_0_OR_GREATER
            ArgumentNullException.ThrowIfNull(argument, paramName);
#else
            if (argument is null)
            {
                Throw(paramName);
            }
#endif
        }

#if !NET6_0_OR_GREATER
        [DoesNotReturn]
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Throw(string? paramName) => throw new ArgumentNullException(paramName);
#endif

        [Obsolete("ArgumentNullException.ThrowIfNull is not applicable for value types.", true)]
        public static void ThrowIfNull<T>(
            T? argument,
            [CallerArgumentExpression(nameof(argument))] string? paramName = null)
            where T : struct
        {
        }
    }
}
