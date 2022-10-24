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
using Orleans.Serialization.Cloning;

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
        /// Reads a field header.
        /// </summary>
        /// <typeparam name="TInput">The reader input type.</typeparam>
        /// <param name="reader">The reader.</param>
        /// <param name="header">The header.</param>
        /// <param name="id">The identifier.</param>
        /// <returns>The field id, if a new field header was written, otherwise <c>-1</c>.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int ReadHeader<TInput>(ref Reader<TInput> reader, scoped ref Field header, int id)
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
        public static int ReadHeaderExpectingEndBaseOrEndObject<TInput>(ref Reader<TInput> reader, scoped ref Field header, int id)
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
        /// <param name="writer">The writer.</param>
        /// <param name="fieldIdDelta">The field identifier delta.</param>
        /// <param name="expectedType">The expected type.</param>
        /// <param name="value">The value.</param>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void SerializeUnexpectedType<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, object value) where TBufferWriter : IBufferWriter<byte>
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
        public static TField DeserializeUnexpectedType<TInput, TField>(ref Reader<TInput> reader, Field field) where TField : class
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
        /// Default copier for shallow-copyable types
        /// </summary>
        public sealed class DefaultShallowCopier<T> : IDeepCopier<T>
        {
            public T DeepCopy(T input, CopyContext _) => input;
        }

        /// <summary>
        /// Default codec implementation for abstract classes
        /// </summary>
        public abstract class AbstractCodec<T> : IFieldCodec<T>, IBaseCodec<T> where T : class
        {
            public void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, T value) where TBufferWriter : IBufferWriter<byte>
            {
                if (value is null)
                {
                    ReferenceCodec.WriteNullReference(ref writer, fieldIdDelta);
                }
                else
                {
                    writer.Session.CodecProvider.GetCodec(value.GetType()).WriteField(ref writer, fieldIdDelta, expectedType, value);
                }
            }

            public T ReadValue<TReaderInput>(ref Reader<TReaderInput> reader, Field field)
            {
                if (field.WireType == WireType.Reference)
                    return ReferenceCodec.ReadReference<T, TReaderInput>(ref reader, field);

                return (T)reader.Session.CodecProvider.GetCodec(field.FieldType).ReadValue(ref reader, field);
            }

            public virtual void Serialize<TBufferWriter>(ref Writer<TBufferWriter> writer, T instance) where TBufferWriter : IBufferWriter<byte> { }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public virtual void Deserialize<TReaderInput>(ref Reader<TReaderInput> reader, T instance)
            {
                var id = 0;
                Field header = default;
                while (true)
                {
                    id = ReadHeaderExpectingEndBaseOrEndObject(ref reader, ref header, id);
                    if (id == -1)
                        break;

                    reader.ConsumeUnknownField(header);
                }
            }
        }
    }
}
