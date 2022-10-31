using System;
using System.Buffers;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization.Activators;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.WireProtocol;

namespace Orleans.Serialization.GeneratedCodeHelpers
{
    /// <summary>
    /// Utilities for use by generated code.
    /// </summary>
    public static class OrleansGeneratedCodeHelper
    {
        private static readonly ThreadLocal<RecursiveServiceResolutionState> ResolutionState = new ThreadLocal<RecursiveServiceResolutionState>(() => new RecursiveServiceResolutionState());

        private sealed class RecursiveServiceResolutionState
        {
            private int _depth;

            public List<object> Callers { get; } = new List<object>();

            public void Enter(object caller)
            {
                ++_depth;
                if (caller is not null)
                {
                    Callers.Add(caller);
                }
            }

            public void Exit()
            {
                if (--_depth <= 0)
                {
                    Callers.Clear();
                }
            }
        }

        /// <summary>
        /// Unwraps the provided service if it was wrapped.
        /// </summary>
        /// <typeparam name="TService">The service type.</typeparam>
        /// <param name="caller">The caller.</param>
        /// <param name="codecProvider">The codec provider.</param>
        /// <returns>The unwrapped service.</returns>
        public static TService GetService<TService>(object caller, ICodecProvider codecProvider)
        {
            var state = ResolutionState.Value;

            try
            {
                state.Enter(caller);


                foreach (var c in state.Callers)
                {
                    if (c is TService s && !(c is IServiceHolder<TService>))
                    {
                        return s;
                    }
                }

                var val = ActivatorUtilities.GetServiceOrCreateInstance<TService>(codecProvider.Services);
                while (val is IServiceHolder<TService> wrapping)
                {
                    val = wrapping.Value;
                }

                return val;
            }
            finally
            {
                state.Exit();
            }
        }

        /// <summary>
        /// Unwraps the provided service if it was wrapped.
        /// </summary>
        /// <typeparam name="TService">The service type.</typeparam>
        /// <param name="caller">The caller.</param>
        /// <param name="service">The service.</param>
        /// <returns>The unwrapped service.</returns>
        public static TService UnwrapService<TService>(object caller, TService service)
        {
            var state = ResolutionState.Value;

            try
            {
                state.Enter(caller);

                foreach (var c in state.Callers)
                {
                    if (c is TService s and not IServiceHolder<TService>)
                    {
                        return s;
                    }
                }

                return Unwrap(service);
            }
            finally
            {
                state.Exit();
            }

            static TService Unwrap(TService val)
            {
                while (val is IServiceHolder<TService> wrapping)
                {
                    val = wrapping.Value;
                }

                return val;
            }
        }

        internal static object TryGetService(Type serviceType)
        {
            var state = ResolutionState.Value;
            foreach (var c in state.Callers)
            {
                var type = c?.GetType();
                if (serviceType == type)
                {
                    return c;
                }
            }

            return null;
        }

        /// <summary>
        /// Returns the provided copier if it's not shallow-copyable.
        /// </summary>
        public static IDeepCopier<T> GetOptionalCopier<T>(IDeepCopier<T> copier) => copier is IOptionalDeepCopier o && o.IsShallowCopyable() ? null : copier;

        /// <summary>        
        /// Generated code helper method which throws an <see cref="ArgumentOutOfRangeException"/>.
        /// </summary>                
        public static object InvokableThrowArgumentOutOfRange(int index, int maxArgs)
            => throw new ArgumentOutOfRangeException(message: $"The argument index value {index} must be between 0 and {maxArgs}", null);

        /// <summary>
        /// Expects empty content (a single field header of either <see cref="ExtendedWireType.EndBaseFields"/> or <see cref="ExtendedWireType.EndTagDelimited"/>),
        /// but will consume any unexpected fields also.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ConsumeEndBaseOrEndObject<TInput>(this ref Reader<TInput> reader)
        {
            Unsafe.SkipInit(out Field field);
            reader.ReadFieldHeader(ref field);
            reader.ConsumeEndBaseOrEndObject(ref field);
        }

        /// <summary>
        /// Expects empty content (a single field header of either <see cref="ExtendedWireType.EndBaseFields"/> or <see cref="ExtendedWireType.EndTagDelimited"/>),
        /// but will consume any unexpected fields also.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ConsumeEndBaseOrEndObject<TInput>(this ref Reader<TInput> reader, scoped ref Field field)
        {
            if (!field.IsEndBaseOrEndObject)
                ConsumeUnexpectedContent(ref reader, ref field);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ConsumeUnexpectedContent<TInput>(this ref Reader<TInput> reader, scoped ref Field field)
        {
            do
            {
                reader.ConsumeUnknownField(ref field);
                reader.ReadFieldHeader(ref field);
            } while (!field.IsEndBaseOrEndObject);
        }

        /// <summary>
        /// Serializes an unexpected value.
        /// </summary>
        /// <typeparam name="TBufferWriter">The buffer writer type.</typeparam>
        /// <param name="writer">The writer.</param>
        /// <param name="fieldIdDelta">The field identifier delta.</param>
        /// <param name="expectedType">The expected type.</param>
        /// <param name="value">The value.</param>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void SerializeUnexpectedType<TBufferWriter>(this ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, object value) where TBufferWriter : IBufferWriter<byte>
        {
            var specificSerializer = writer.Session.CodecProvider.GetCodec(value.GetType());
            specificSerializer.WriteField(ref writer, fieldIdDelta, expectedType, value);
        }

        /// <summary>
        /// Deserializes an unexpected value.
        /// </summary>
        /// <typeparam name="TInput">The reader input type.</typeparam>
        /// <typeparam name="TField">The value type.</typeparam>
        /// <param name="reader">The reader.</param>
        /// <param name="field">The field.</param>
        /// <returns>The value.</returns>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static TField DeserializeUnexpectedType<TInput, TField>(this ref Reader<TInput> reader, scoped ref Field field) where TField : class
        {
            var specificSerializer = reader.Session.CodecProvider.GetCodec(field.FieldType);
            return (TField)specificSerializer.ReadValue(ref reader, field);
        }

        /// <summary>
        /// Gets the <see cref="MethodInfo"/> matching the provided values.
        /// </summary>
        /// <param name="interfaceType">Type of the interface.</param>
        /// <param name="methodName">Name of the method.</param>
        /// <param name="methodTypeParameters">The method type parameters.</param>
        /// <param name="parameterTypes">The parameter types.</param>
        /// <returns>The corresponding <see cref="MethodInfo"/>.</returns>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static MethodInfo GetMethodInfoOrDefault(Type interfaceType, string methodName, Type[] methodTypeParameters, Type[] parameterTypes)
        {
            if (interfaceType is null)
            {
                return null;
            }

            foreach (var method in interfaceType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                var current = method;
                if (current.Name != methodName)
                {
                    continue;
                }

                if (current.ContainsGenericParameters != methodTypeParameters is { Length: > 0 })
                {
                    continue;
                }

                if (methodTypeParameters is { Length: > 0 })
                {
                    if (methodTypeParameters.Length != current.GetGenericArguments().Length)
                    {
                        continue;
                    }

                    current = current.MakeGenericMethod(methodTypeParameters);
                }

                var parameters = current.GetParameters();
                if (parameters.Length != (parameterTypes?.Length ?? 0))
                {
                    continue;
                }

                var isMatch = true;
                for (int i = 0; i < parameters.Length; i++)
                {
                    if (!parameters[i].ParameterType.Equals(parameterTypes[i]))
                    {
                        isMatch = false;
                        break;
                    }
                }

                if (!isMatch)
                {
                    continue;
                }

                return current;
            }

            foreach (var implemented in interfaceType.GetInterfaces())
            {
                if (GetMethodInfoOrDefault(implemented, methodName, methodTypeParameters, parameterTypes) is { } method)
                {
                    return method;
                }
            }

            return null;
        }

        /// <summary>
        /// Default copier implementation for (rarely copied) exception classes
        /// </summary>
        public abstract class ExceptionCopier<T, B> : IDeepCopier<T>, IBaseCopier<T> where T : B where B : Exception
        {
            private readonly IActivator<T> _activator;
            private readonly IBaseCopier<B> _baseTypeCopier;

            protected ExceptionCopier(ICodecProvider codecProvider)
            {
                _activator = GetService<IActivator<T>>(this, codecProvider);
                _baseTypeCopier = GetService<IBaseCopier<B>>(this, codecProvider);
            }

            public T DeepCopy(T original, CopyContext context)
            {
                if (original is null)
                    return null;

                if (original.GetType() != typeof(T))
                    return context.DeepCopy(original);

                var result = _activator.Create();
                DeepCopy(original, result, context);
                return result;
            }

            public virtual void DeepCopy(T input, T output, CopyContext context) => _baseTypeCopier.DeepCopy(input, output, context);
        }
    }
}
