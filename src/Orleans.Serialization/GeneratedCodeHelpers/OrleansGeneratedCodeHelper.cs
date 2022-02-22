using Orleans.Serialization.Buffers;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.WireProtocol;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

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
                if (caller is object)
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

                return Unwrap(ActivatorUtilities.GetServiceOrCreateInstance<TService>(codecProvider.Services));
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

        /// <summary>
        /// Unwraps the provided service if it was wrapped.
        /// </summary>
        /// <typeparam name="TService">The service type.</typeparam>
        /// <param name="caller">The caller.</param>
        /// <param name="service">The service.</param>
        /// <returns>The unwrapped service.</returns>
        public static TService UnwrapService<TService>(object caller, TService service)
        {
            while (service is IServiceHolder<TService> && caller is TService callerService)
            {
                return callerService;
            }

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
        /// Generated code helper method which throws an <see cref="ArgumentOutOfRangeException"/>.
        /// </summary>                
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static TArgument InvokableThrowArgumentOutOfRange<TArgument>(int index, int maxArgs) => throw new ArgumentOutOfRangeException($"The argument index value {index} must be between 0 and {maxArgs}");

        /// <summary>
        /// Reads a field header.
        /// </summary>
        /// <typeparam name="TInput">The reader input type.</typeparam>
        /// <param name="reader">The reader.</param>
        /// <param name="header">The header.</param>
        /// <param name="id">The identifier.</param>
        /// <returns>The field id, if a new field header was written, otherwise <c>-1</c>.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int ReadHeader<TInput>(ref Reader<TInput> reader, ref Field header, int id)
        {
            reader.ReadFieldHeader(ref header);
            if (header.IsEndBaseOrEndObject)
            {
                return -1;
            }

            return (int)(id + header.FieldIdDelta);
        }

        /// <summary>
        /// Reads the header expecting an end base tag or end object tag.
        /// </summary>
        /// <typeparam name="TInput">The reader input type.</typeparam>
        /// <param name="reader">The reader.</param>
        /// <param name="header">The header.</param>
        /// <param name="id">The identifier.</param>
        /// <returns>The field id, if a new field header was written, otherwise <c>-1</c>.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int ReadHeaderExpectingEndBaseOrEndObject<TInput>(ref Reader<TInput> reader, ref Field header, int id)
        {
            reader.ReadFieldHeader(ref header);
            if (header.IsEndBaseOrEndObject)
            {
                return -1;
            }

            return (int)(id + header.FieldIdDelta);
        }

        /// <summary>
        /// Serializes an unexpected value.
        /// </summary>
        /// <typeparam name="TBufferWriter">The buffer writer type.</typeparam>
        /// <typeparam name="TField">The value type.</typeparam>
        /// <param name="writer">The writer.</param>
        /// <param name="fieldIdDelta">The field identifier delta.</param>
        /// <param name="expectedType">The expected type.</param>
        /// <param name="value">The value.</param>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void SerializeUnexpectedType<TBufferWriter, TField>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, TField value) where TBufferWriter : IBufferWriter<byte>
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
        public static TField DeserializeUnexpectedType<TInput, TField>(ref Reader<TInput> reader, Field field)
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
                if (method.Name != methodName)
                {
                    continue;
                }

                if (!method.ContainsGenericParameters && methodTypeParameters is { Length: > 0 })
                {
                    continue;
                }

                if (method.ContainsGenericParameters && methodTypeParameters is null or { Length: 0 })
                {
                    continue;
                }

                var parameters = method.GetParameters();
                if (parameters.Length != parameterTypes.Length)
                {
                    continue;
                }

                for (int i = 0; i < parameters.Length; i++)
                {
                    if (!parameters[0].ParameterType.Equals(parameterTypes[i]))
                    {
                        continue;
                    }
                }

                return method;
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
    }
}