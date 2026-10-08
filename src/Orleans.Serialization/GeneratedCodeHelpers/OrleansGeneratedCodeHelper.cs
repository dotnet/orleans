using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
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

        internal static void EnterServiceResolution(ICodecProvider provider, CodecProvider.ConstructionScope scope)
            => ResolutionState.Value!.Enter(null!, provider, scope);
        internal static void ExitServiceResolution() => ResolutionState.Value!.Exit();
        internal static CodecProvider.ConstructionScope? GetConstructionScope(ICodecProvider provider)
            => ResolutionState.Value!.GetConstructionScope(provider);

        private sealed class RecursiveServiceResolutionState
        {
            private int _depth;

            public List<(object Caller, ICodecProvider? Provider)> Callers { get; } = new();
            private readonly List<(ICodecProvider? Provider, int CallerStart, CodecProvider.ConstructionScope? Scope)> _active = new();
            public ICodecProvider? Provider => _active.Count > 0 ? _active[^1].Provider : null;

            public CodecProvider.ConstructionScope? GetConstructionScope(ICodecProvider provider)
            {
                foreach (var frame in _active)
                {
                    if (frame.Scope is not null && ReferenceEquals(frame.Provider, provider)) return frame.Scope;
                }
                return null;
            }

            public void Enter(object caller, ICodecProvider? provider = null, CodecProvider.ConstructionScope? scope = null)
            {
                ++_depth;
                var owner = provider ?? Provider;
                var callerOwner = Provider ?? owner;
                var callerStart = ReferenceEquals(owner, Provider) ? -1 : Callers.Count;
                _active.Add((owner, callerStart, scope));
                if (caller is not null)
                {
                    Callers.Add((caller, callerOwner));
                }
            }

            public void Exit()
            {
                var callerStart = _active[^1].CallerStart;
                _active.RemoveAt(_active.Count - 1);
                if (callerStart >= 0)
                {
                    Callers.RemoveRange(callerStart, Callers.Count - callerStart);
                }
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
        public static TService GetService<
#if NET5_0_OR_GREATER
            [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TService>(
#else
            TService>(
#endif
            object caller,
            ICodecProvider codecProvider)
        {
            ArgumentNullExceptionPolyfill.ThrowIfNull(codecProvider);

            var state = ResolutionState.Value!;

            try
            {
                state.Enter(caller, codecProvider);


                foreach (var c in state.Callers)
                {
                    if (ReferenceEquals(c.Provider, codecProvider) && c.Caller is TService s && !(c.Caller is IServiceHolder<TService>))
                    {
                        return s;
                    }
                }

                TService val;
                if (codecProvider is CodecProvider provider)
                {
                    if (provider.TryGetSerializerService(typeof(TService), out var registered))
                    {
                        return (TService)registered;
                    }

                }

                val = ActivatorUtilities.GetServiceOrCreateInstance<TService>(codecProvider.Services);
                while (val is IServiceHolder<TService> wrapping)
                {
                    val = wrapping.Value;
                }

                return val;
            }
            catch (Exception exception)
            {
                if (codecProvider is CodecProvider provider) provider.RecordConstructionFailure(exception);
                throw;
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
            var state = ResolutionState.Value!;
            var callerProvider = state.Provider;
            var serviceProvider = service is IServiceHolder<TService> holder ? holder.Provider as ICodecProvider : null;

            try
            {
                state.Enter(caller, serviceProvider);

                foreach (var c in state.Callers)
                {
                    if (ReferenceEquals(c.Provider, state.Provider) && c.Caller is TService s and not IServiceHolder<TService>)
                    {
                        return s;
                    }
                }

                var result = Unwrap(service);
                return result;
            }
            catch (Exception exception)
            {
                if ((callerProvider ?? state.Provider) is CodecProvider provider) provider.RecordConstructionFailure(exception);
                throw;
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

        internal static object? TryGetService(Type serviceType, ICodecProvider codecProvider)
        {
            var state = ResolutionState.Value!;
            foreach (var c in state.Callers)
            {
                if (ReferenceEquals(c.Provider, codecProvider) && c.Caller is not IServiceHolder<object>
                    && serviceType.IsInstanceOfType(c.Caller))
                {
                    return c.Caller;
                }
            }

            return null;
        }

        /// <summary>
        /// Returns the provided copier if it's not shallow-copyable.
        /// </summary>
        public static IDeepCopier<T>? GetOptionalCopier<T>(IDeepCopier<T> copier) => copier is IOptionalDeepCopier o && o.IsShallowCopyable() ? null : copier;

        internal static bool IsShallowCopyable([NotNullWhen(false)] IDeepCopier? copier)
            => copier is null || copier is IOptionalDeepCopier optional && optional.IsShallowCopyable();

        internal static bool IsShallowCopyable<TCopier>(ref int cache, TCopier copier, Func<TCopier, bool> calculate)
        {
            var result = Volatile.Read(ref cache);
            if (result == 0)
            {
                result = calculate(copier) ? 1 : 2;
                Volatile.Write(ref cache, result);
            }

            return result == 1;
        }

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
        public static void SerializeUnexpectedType<TBufferWriter>(this ref Writer<TBufferWriter> writer, uint fieldIdDelta, [System.Diagnostics.CodeAnalysis.AllowNull] Type expectedType, object value) where TBufferWriter : IBufferWriter<byte>
        {
            ArgumentNullExceptionPolyfill.ThrowIfNull(value);

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
            var specificSerializer = reader.Session.CodecProvider.GetCodec(field.FieldType!);
            return (TField)specificSerializer.ReadValue(ref reader, field)!;
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
        public static MethodInfo? GetMethodInfoOrDefault(
            Type? interfaceType,
            string methodName,
            Type[]? methodTypeParameters,
            Type[]? parameterTypes)
            => GetMethodInfoOrDefaultCore(interfaceType, methodName, methodTypeParameters, parameterTypes);

#if NET5_0_OR_GREATER
        [UnconditionalSuppressMessage(
            "Trimming",
            "IL2070",
            Justification = "Generated manifests register grain interfaces through TypeManifestOptions.AddInterface, which preserves public and non-public methods and inherited interfaces. Recursive Type.GetInterfaces() traversal cannot propagate that annotation.")]
#endif
        private static MethodInfo? GetMethodInfoOrDefaultCore(
            Type? interfaceType,
            string methodName,
            Type[]? methodTypeParameters,
            Type[]? parameterTypes)
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
                    if (!parameters[i].ParameterType.Equals(parameterTypes![i]))
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
                if (GetMethodInfoOrDefaultCore(implemented, methodName, methodTypeParameters, parameterTypes) is { } method)
                {
                    return method;
                }
            }

            return null;
        }

        /// <summary>
        /// Default copier implementation for exception types.
        /// </summary>
        /// <typeparam name="T">The exception type being copied.</typeparam>
        /// <typeparam name="B">The base exception type copied by the base copier.</typeparam>
        public abstract class ExceptionCopier<T, B> : IDeepCopier<T>, IBaseCopier<T> where T : B where B : Exception
        {
            private readonly Type _fieldType = typeof(T);
            private readonly IActivator<T> _activator;
            private readonly IBaseCopier<B> _baseTypeCopier;

            /// <summary>
            /// Initializes a new instance of the <see cref="ExceptionCopier{T, B}"/> class.
            /// </summary>
            /// <param name="codecProvider">The codec provider used to resolve the activator and base copier.</param>
            protected ExceptionCopier(ICodecProvider codecProvider)
            {
                _activator = GetService<IActivator<T>>(this, codecProvider);
                _baseTypeCopier = GetService<IBaseCopier<B>>(this, codecProvider);
            }

            /// <inheritdoc/>
            [return: NotNullIfNotNull(nameof(original))]
            [SuppressMessage("Design", "CA1062:Validate arguments of public methods", Justification = "The Orleans deep-copy pipeline supplies the active non-null copy context.")]
            public T? DeepCopy(T? original, CopyContext context)
            {
                if (original is null)
                {
                    return default;
                }

                if (original.GetType() != _fieldType)
                {
                    return context.DeepCopy(original)!;
                }

                var result = _activator.Create();
                DeepCopy(original, result, context);
                return result;
            }

            /// <inheritdoc/>
            public virtual void DeepCopy(T input, T output, CopyContext context) => _baseTypeCopier.DeepCopy(input, output, context);
        }
    }
}
