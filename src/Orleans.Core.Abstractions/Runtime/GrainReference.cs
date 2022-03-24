using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Invocation;
using Orleans.Serialization.Serializers;
using Microsoft.Extensions.DependencyInjection;
using Orleans.CodeGeneration;
using System.Text;
using System.Diagnostics;

namespace Orleans.Runtime
{
    /// <summary>
    /// Properties common to <see cref="GrainReference"/> instances with the same <see cref="GrainType"/> and <see cref="GrainInterfaceType"/>.
    /// </summary>
    public class GrainReferenceShared
    {
        public GrainReferenceShared(
            GrainType grainType,
            GrainInterfaceType grainInterfaceType,
            ushort interfaceVersion,
            IGrainReferenceRuntime runtime,
            InvokeMethodOptions invokeMethodOptions,
            IServiceProvider serviceProvider)
        {
            this.GrainType = grainType;
            this.InterfaceType = grainInterfaceType;
            this.Runtime = runtime;
            this.InvokeMethodOptions = invokeMethodOptions;
            this.ServiceProvider = serviceProvider;
            this.InterfaceVersion = interfaceVersion;
        }

        /// <summary>
        /// Gets the grain reference runtime.
        /// </summary>
        public IGrainReferenceRuntime Runtime { get; }

        /// <summary>
        /// Gets the grain type.
        /// </summary>
        public GrainType GrainType { get; }

        /// <summary>
        /// Gets the interface type.
        /// </summary>
        public GrainInterfaceType InterfaceType { get; }

        /// <summary>
        /// Gets the common invocation options.
        /// </summary>
        public InvokeMethodOptions InvokeMethodOptions { get; }

        /// <summary>
        /// Gets the service provider.
        /// </summary>
        public IServiceProvider ServiceProvider { get; }

        /// <summary>
        /// Gets the interface version.
        /// </summary>
        public ushort InterfaceVersion { get; }
    }

    /// <summary>
    /// Functionality for serializing and deserializing <see cref="GrainReference"/> and derived types.
    /// </summary>
    [RegisterSerializer]
    internal class GrainReferenceCodec : GeneralizedReferenceTypeSurrogateCodec<IAddressable, GrainReferenceSurrogate>
    {
        private readonly IGrainFactory _grainFactory;

        /// <summary>
        /// Initializes a new instance of the <see cref="GrainReferenceCodec"/> class.
        /// </summary>
        /// <param name="grainFactory">The grain factory.</param>
        /// <param name="surrogateSerializer">The serializer for the surrogate type used by this class.</param>
        public GrainReferenceCodec(IGrainFactory grainFactory, IValueSerializer<GrainReferenceSurrogate> surrogateSerializer)
            : base(surrogateSerializer)
        {
            _grainFactory = grainFactory;
        }

        /// <inheritdoc/>
        public override IAddressable ConvertFromSurrogate(ref GrainReferenceSurrogate surrogate)
        {
            return _grainFactory.GetGrain(surrogate.GrainId, surrogate.GrainInterfaceType);
        }

        /// <inheritdoc/>
        public override void ConvertToSurrogate(IAddressable value, ref GrainReferenceSurrogate surrogate)
        {
            var refValue = value.AsReference();
            surrogate = new GrainReferenceSurrogate
            {
                GrainId = refValue.GrainId,
                GrainInterfaceType = refValue.InterfaceType
            };
        }
    }

    /// <summary>
    /// Copier implementation for <see cref="GrainReference"/> and derived classes.
    /// </summary>
    [RegisterCopier]
    internal class GrainReferenceCopier : IDeepCopier<GrainReference>, IDerivedTypeCopier
    {
        /// <inheritdoc/>
        public GrainReference DeepCopy(GrainReference input, CopyContext context) => input;
    }

    /// <summary>
    /// Provides specialized copier instances for grain reference types.
    /// </summary>
    internal class GrainReferenceCopierProvider : ISpecializableCopier
    {
        /// <inheritdoc/>
        public IDeepCopier GetSpecializedCopier(Type type) => (IDeepCopier)Activator.CreateInstance(typeof(TypedGrainReferenceCopier<>).MakeGenericType(type));

        /// <inheritdoc/>
        public bool IsSupportedType(Type type) => typeof(IAddressable).IsAssignableFrom(type) && type.IsInterface;
    }

    /// <summary>
    /// A strongly-typed copier for grain reference instances.
    /// </summary>
    /// <typeparam name="TInterface">The grain interface type.</typeparam>
    internal class TypedGrainReferenceCopier<TInterface> : IDeepCopier<TInterface>
    {
        /// <inheritdoc/>
        public TInterface DeepCopy(TInterface input, CopyContext context)
        {
            if (input is null) return input;
            if (input is GrainReference) return input;
            if (input is IGrainObserver observer)
            {
                GrainReferenceCodecProvider.ThrowGrainObserverInvalidException(observer);
            }

            var addressable = (IAddressable)input;
            var grainReference = addressable.AsReference();
            return (TInterface)grainReference.Runtime.Cast(addressable, typeof(TInterface));
        }
    }

    /// <summary>
    /// Provides specialized codec instances for grain reference types.
    /// </summary>
    internal class GrainReferenceCodecProvider : ISpecializableCodec
    {
        private readonly IServiceProvider _serviceProvider;

        /// <summary>
        /// Initializes a new instance of the <see cref="GrainReferenceCodecProvider"/> class.
        /// </summary>
        /// <param name="serviceProvider">The service provider.</param>
        public GrainReferenceCodecProvider(IServiceProvider serviceProvider) => _serviceProvider = serviceProvider;

        /// <inheritdoc/>
        public IFieldCodec GetSpecializedCodec(Type type) => (IFieldCodec)ActivatorUtilities.GetServiceOrCreateInstance(_serviceProvider, typeof(TypedGrainReferenceCodec<>).MakeGenericType(type));

        /// <inheritdoc/>
        public bool IsSupportedType(Type type) => typeof(IAddressable).IsAssignableFrom(type);

        /// <summary>
        /// Throws an exception indicating that a parameter type is not supported.
        /// </summary>
        /// <param name="observer">The observer.</param>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void ThrowGrainObserverInvalidException(IGrainObserver observer)
            => throw new NotSupportedException($"IGrainObserver parameters must be GrainReference or Grain and cannot be type {observer.GetType()}. Did you forget to CreateObjectReference?");
    }

    /// <summary>
    /// A strongly-typed codec for grain reference instances.
    /// </summary>
    /// <typeparam name="T">The grain reference interface type.</typeparam>
    internal class TypedGrainReferenceCodec<T> : GeneralizedReferenceTypeSurrogateCodec<T, GrainReferenceSurrogate>
        where T : class, IAddressable
    {
        private readonly IGrainFactory _grainFactory;

        /// <summary>
        /// Initializes a new instance of the <see cref="TypedGrainReferenceCodec{T}"/> class.
        /// </summary>
        /// <param name="grainFactory">The grain factory.</param>
        /// <param name="surrogateSerializer">The surrogate serializer.</param>
        public TypedGrainReferenceCodec(IGrainFactory grainFactory, IValueSerializer<GrainReferenceSurrogate> surrogateSerializer) : base(surrogateSerializer)
        {
            _grainFactory = grainFactory;
        }

        /// <inheritdoc/>
        public override T ConvertFromSurrogate(ref GrainReferenceSurrogate surrogate)
        {
            return (T)_grainFactory.GetGrain(surrogate.GrainId, surrogate.GrainInterfaceType);
        }

        /// <inheritdoc/>
        public override void ConvertToSurrogate(T value, ref GrainReferenceSurrogate surrogate)
        {
            // Check that the typical case is false before performing the more expensive interface check
            if (value is not GrainReference refValue)
            {
                if (value is IGrainObserver observer)
                {
                    GrainReferenceCodecProvider.ThrowGrainObserverInvalidException(observer);
                }

                refValue = (GrainReference)(object)value.AsReference<T>();
            }

            surrogate = new GrainReferenceSurrogate
            {
                GrainId = refValue.GrainId,
                GrainInterfaceType = refValue.InterfaceType
            };
        }
    }

    /// <summary>
    /// A surrogate used to represent <see cref="GrainReference"/> implementations for serialization.
    /// </summary>
    [GenerateSerializer]
    internal struct GrainReferenceSurrogate
    {
        /// <summary>
        /// Gets or sets the grain id.
        /// </summary>
        [Id(1)]
        public GrainId GrainId { get; set; }

        /// <summary>
        /// Gets or sets the grain interface type.
        /// </summary>
        [Id(2)]
        public GrainInterfaceType GrainInterfaceType { get; set; }
    }

    /// <summary>
    /// This is the base class for all grain references.
    /// </summary>
    [DefaultInvokableBaseType(typeof(ValueTask<>), typeof(Request<>))]
    [DefaultInvokableBaseType(typeof(ValueTask), typeof(Request))]
    [DefaultInvokableBaseType(typeof(Task<>), typeof(TaskRequest<>))]
    [DefaultInvokableBaseType(typeof(Task), typeof(TaskRequest))]
    [DefaultInvokableBaseType(typeof(void), typeof(VoidRequest))]
    public class GrainReference : IAddressable, IEquatable<GrainReference>
    {
        /// <summary>
        /// The grain reference functionality which is shared by all grain references of a given type.
        /// </summary>
        [NonSerialized]
        private GrainReferenceShared _shared;

        /// <summary>
        /// The underlying grain id key.
        /// </summary>
        [NonSerialized]
        private IdSpan _key;

        /// <summary>
        /// Gets the grain reference functionality which is shared by all grain references of a given type.
        /// </summary>
        internal GrainReferenceShared Shared => _shared ?? throw new GrainReferenceNotBoundException(this);

        /// <summary>
        /// Gets the grain reference runtime.
        /// </summary>
        internal IGrainReferenceRuntime Runtime => Shared.Runtime;

        /// <summary>
        /// Gets the grain id.
        /// </summary>
        public GrainId GrainId => GrainId.Create(_shared.GrainType, _key);

        /// <summary>
        /// Gets the interface type.
        /// </summary>
        public GrainInterfaceType InterfaceType => _shared.InterfaceType;

        /// <summary>Initializes a new instance of the <see cref="GrainReference"/> class.</summary>
        /// <param name="shared">
        /// The grain reference functionality which is shared by all grain references of a given type.
        /// </param>
        /// <param name="key">
        /// The key portion of the grain id.
        /// </param>
        protected GrainReference(GrainReferenceShared shared, IdSpan key)
        {
            _shared = shared;
            _key = key;
        }

        /// <summary>Initializes a new instance of the <see cref="GrainReference"/> class.</summary>
        /// <param name="shared">
        /// The grain reference functionality which is shared by all grain references of a given type.
        /// </param>
        /// <param name="grainId">
        /// The grain id.
        /// </param>
        /// <returns>
        /// A new <see cref="GrainReference"/> instance.
        /// </returns>
        internal static GrainReference FromGrainId(GrainReferenceShared shared, GrainId grainId)
        {
            return new GrainReference(shared, grainId.Key);
        }

        /// <summary>
        /// Creates a new grain reference which implements the specified grain interface.
        /// </summary>
        /// <typeparam name="TGrainInterface">
        /// The grain interface type.
        /// </typeparam>
        /// <returns>A new grain reference which implements the specified interface type.</returns>
        public virtual TGrainInterface Cast<TGrainInterface>()
            where TGrainInterface : IAddressable
            => (TGrainInterface)_shared.Runtime.Cast(this, typeof(TGrainInterface));

        /// <summary>
        /// Tests this reference for equality to another object.
        /// Two grain references are equal if they both refer to the same grain.
        /// </summary>
        /// <param name="obj">The object to test for equality against this reference.</param>
        /// <returns><c>true</c> if the object is equal to this reference.</returns>
        public override bool Equals(object obj)
        {
            return Equals(obj as GrainReference);
        }

        /// <inheritdoc />
        public bool Equals(GrainReference other) => other is GrainReference && this.GrainId.Equals(other.GrainId);

        /// <inheritdoc />
        public override int GetHashCode() => this.GrainId.GetHashCode();

        /// <summary>
        /// Get a uniform hash code for this grain reference.
        /// </summary>
        /// <returns>
        /// The uniform hash code.
        /// </returns>
        public uint GetUniformHashCode()
        {
            // GrainId already includes the hashed type code for generic arguments.
            return GrainId.GetUniformHashCode();
        }

        /// <summary>
        /// Compares two references for equality.
        /// Two grain references are equal if they both refer to the same grain.
        /// </summary>
        /// <param name="reference1">First grain reference to compare.</param>
        /// <param name="reference2">Second grain reference to compare.</param>
        /// <returns><c>true</c> if both grain references refer to the same grain (by grain identifier).</returns>
        public static bool operator ==(GrainReference reference1, GrainReference reference2)
        {
            if (reference1 is null) return reference2 is null;

            return reference1.Equals(reference2);
        }

        /// <summary>
        /// Compares two references for inequality.
        /// Two grain references are equal if they both refer to the same grain.
        /// </summary>
        /// <param name="reference1">First grain reference to compare.</param>
        /// <param name="reference2">Second grain reference to compare.</param>
        /// <returns><c>false</c> if both grain references are resolved to the same grain (by grain identifier).</returns>
        public static bool operator !=(GrainReference reference1, GrainReference reference2)
        {
            if (reference1 is null) return !(reference2 is null);


            return !reference1.Equals(reference2);
        }

        /// <summary>
        /// Gets the interface version.
        /// </summary>
        public ushort InterfaceVersion => Shared.InterfaceVersion;

        /// <summary>
        /// Gets the interface name.
        /// </summary>
        public virtual string InterfaceName => InterfaceType.ToStringUtf8();

        /// <inheritdoc/>
        public override string ToString() => $"GrainReference:{GrainId}:{InterfaceType}";

        protected TInvokable GetInvokable<TInvokable>() => ActivatorUtilities.GetServiceOrCreateInstance<TInvokable>(Shared.ServiceProvider);

        /// <summary>
        /// Invokes the provided method.
        /// </summary>
        /// <typeparam name="T">The underlying method return type.</typeparam>
        /// <param name="methodDescription">The method description.</param>
        /// <returns>The result of the invocation.</returns>
        protected ValueTask<T> InvokeAsync<T>(IInvokable methodDescription)
        {
            var request = (RequestBase)methodDescription;
            return this.Runtime.InvokeMethodAsync<T>(this, methodDescription, request.Options);
        }

        /// <summary>
        /// Invokes the provided method.
        /// </summary>
        /// <param name="methodDescription">The method description.</param>
        /// <returns>A <see cref="ValueTask"/> representing the operation.</returns>
        protected ValueTask InvokeAsync(IInvokable methodDescription)
        {
            var request = (RequestBase)methodDescription;
            return this.Runtime.InvokeMethodAsync(this, methodDescription, request.Options);
        }
    }

    /// <summary>
    /// Base type used for method requests.
    /// </summary>
    [GenerateSerializer]
    public abstract class RequestBase : IInvokable
    {
        /// <summary>
        /// Gets the invocation options.
        /// </summary>
        [field: NonSerialized]
        public InvokeMethodOptions Options { get; private set; }

        /// <inheritdoc/>
        public abstract int ArgumentCount { get; }

        /// <summary>
        /// Incorporates the provided invocation options.
        /// </summary>
        /// <param name="options">
        /// The options.
        /// </param>
        public void AddInvokeMethodOptions(InvokeMethodOptions options)
        {
            Options |= options;
        }

        /// <inheritdoc/>
        [DebuggerHidden]
        public abstract ValueTask<Response> Invoke();

        /// <inheritdoc/>
        public abstract TTarget GetTarget<TTarget>();

        /// <inheritdoc/>
        public abstract void SetTarget<TTargetHolder>(TTargetHolder holder)
            where TTargetHolder : ITargetHolder;

        /// <inheritdoc/>
        public abstract TArgument GetArgument<TArgument>(int index);

        /// <inheritdoc/>
        public abstract void SetArgument<TArgument>(int index, in TArgument value);

        /// <inheritdoc/>
        public abstract void Dispose();

        /// <inheritdoc/>
        public abstract string MethodName { get; }

        /// <inheritdoc/>
        public abstract Type[] MethodTypeArguments { get; }

        /// <inheritdoc/>
        public abstract string InterfaceName { get; }

        /// <inheritdoc/>
        public abstract Type InterfaceType { get; }

        /// <inheritdoc/>
        public abstract Type[] InterfaceTypeArguments { get; }

        /// <inheritdoc/>
        public abstract Type[] ParameterTypes { get; }

        /// <inheritdoc/>
        public abstract MethodInfo Method { get; }

        /// <inheritdoc/>
        public override string ToString()
        {
            var result = new StringBuilder();
            result.Append(InterfaceName);
            if (GetTarget<object>() is { } target)
            {
                result.Append("[(");
                result.Append(InterfaceName);
                result.Append(')');
                result.Append(target.ToString());
                result.Append(']');
            }
            else
            {
                result.Append(InterfaceName);
            }

            result.Append('.');
            result.Append(MethodName);
            result.Append('(');
            for (var n = 0; n < ArgumentCount; n++)
            {
                if (n > 0)
                {
                    result.Append(", ");
                }

                result.Append(GetArgument<object>(n));
            }

            result.Append(')');
            return result.ToString();
        }
    }

    /// <summary>
    /// Base class for requests for methods which return <see cref="ValueTask"/>.
    /// </summary>
    [GenerateSerializer]
    public abstract class Request : RequestBase 
    {
        [DebuggerHidden]
        public override ValueTask<Response> Invoke()
        {
            try
            {
                var resultTask = InvokeInner();
                if (resultTask.IsCompleted)
                {
                    resultTask.GetAwaiter().GetResult();
                    return new ValueTask<Response>(Response.Completed);
                }

                return CompleteInvokeAsync(resultTask);
            }
            catch (Exception exception)
            {
                return new ValueTask<Response>(Response.FromException(exception));
            }
        }

        [DebuggerHidden]
        private static async ValueTask<Response> CompleteInvokeAsync(ValueTask resultTask)
        {
            try
            {
                await resultTask;
                return Response.Completed;
            }
            catch (Exception exception)
            {
                return Response.FromException(exception);
            }
        }

        // Generated
        [DebuggerHidden]
        protected abstract ValueTask InvokeInner();
    }

    /// <summary>
    /// Base class for requests for methods which return <see cref="ValueTask{TResult}"/>.
    /// </summary>
    /// <typeparam name="TResult">
    /// The underlying result type.
    /// </typeparam>
    [GenerateSerializer]
    public abstract class Request<TResult> : RequestBase
    {
        /// <inheritdoc/>
        [DebuggerHidden]
        public override ValueTask<Response> Invoke()
        {
            try
            {
                var resultTask = InvokeInner();
                if (resultTask.IsCompleted)
                {
                    return new ValueTask<Response>(Response.FromResult(resultTask.Result));
                }

                return CompleteInvokeAsync(resultTask);
            }
            catch (Exception exception)
            {
                return new ValueTask<Response>(Response.FromException(exception));
            }
        }

        [DebuggerHidden]
        private static async ValueTask<Response> CompleteInvokeAsync(ValueTask<TResult> resultTask)
        {
            try
            {
                var result = await resultTask;
                return Response.FromResult(result);
            }
            catch (Exception exception)
            {
                return Response.FromException(exception);
            }
        }

        /// <summary>
        /// Invokes the request against the target.
        /// </summary>
        /// <returns>The invocation result.</returns>
        [DebuggerHidden]
        protected abstract ValueTask<TResult> InvokeInner();
    }

    /// <summary>
    /// Base class for requests for methods which return <see cref="Task{TResult}"/>.
    /// </summary>
    /// <typeparam name="TResult">
    /// The underlying result type.
    /// </typeparam>
    [GenerateSerializer]
    public abstract class TaskRequest<TResult> : RequestBase
    {
        /// <inheritdoc/>
        [DebuggerHidden]
        public override ValueTask<Response> Invoke()
        {
            try
            {
                var resultTask = InvokeInner();
                var status = resultTask.Status;
                if (resultTask.IsCompleted)
                {
                    return new ValueTask<Response>(Response.FromResult(resultTask.GetAwaiter().GetResult()));
                }

                return CompleteInvokeAsync(resultTask);
            }
            catch (Exception exception)
            {
                return new ValueTask<Response>(Response.FromException(exception));
            }
        }

        [DebuggerHidden]
        private static async ValueTask<Response> CompleteInvokeAsync(Task<TResult> resultTask)
        {
            try
            {
                var result = await resultTask;
                return Response.FromResult(result);
            }
            catch (Exception exception)
            {
                return Response.FromException(exception);
            }
        }

        /// <summary>
        /// Invokes the request against the target.
        /// </summary>
        /// <returns>The invocation result.</returns>
        [DebuggerHidden]
        protected abstract Task<TResult> InvokeInner();
    }

    /// <summary>
    /// Base class for requests for methods which return <see cref="ValueTask"/>.
    /// </summary>
    [GenerateSerializer]
    public abstract class TaskRequest : RequestBase
    {
        /// <inheritdoc/>
        [DebuggerHidden]
        public override ValueTask<Response> Invoke()
        {
            try
            {
                var resultTask = InvokeInner();
                var status = resultTask.Status;
                if (resultTask.IsCompleted)
                {
                    resultTask.GetAwaiter().GetResult();
                    return new ValueTask<Response>(Response.Completed);
                }

                return CompleteInvokeAsync(resultTask);
            }
            catch (Exception exception)
            {
                return new ValueTask<Response>(Response.FromException(exception));
            }
        }

        [DebuggerHidden]
        private static async ValueTask<Response> CompleteInvokeAsync(Task resultTask)
        {
            try
            {
                await resultTask;
                return Response.Completed;
            }
            catch (Exception exception)
            {
                return Response.FromException(exception);
            }
        }

        /// <summary>
        /// Invokes the request against the target.
        /// </summary>
        /// <returns>The invocation result.</returns>
        [DebuggerHidden]
        protected abstract Task InvokeInner();
    }

    /// <summary>
    /// Base class for requests for void-returning methods.
    /// </summary>
    [GenerateSerializer]
    public abstract class VoidRequest : RequestBase
    {
        /// <inheritdoc/>
        [DebuggerHidden]
        public override ValueTask<Response> Invoke()
        {
            try
            {
                InvokeInner();
                return new ValueTask<Response>(Response.Completed);
            }
            catch (Exception exception)
            {
                return new ValueTask<Response>(Response.FromException(exception));
            }
        }

        /// <summary>
        /// Invokes the request against the target.
        /// </summary>
        [DebuggerHidden]
        protected abstract void InvokeInner();
    }
}
