using System;

namespace Orleans
{
    /// <summary>
    /// When applied to a type, specifies that the type is intended to be serialized and that serialization code should be generated for the type.
    /// </summary>
    /// <seealso cref="System.Attribute" />
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Enum)]
    public sealed class GenerateSerializerAttribute : Attribute
    {
    }

    /// <summary>
    /// When applied to an interface, specifies that support code should be generated to allow remoting of interface calls.
    /// </summary>
    /// <seealso cref="System.Attribute" />
    [AttributeUsage(AttributeTargets.Interface, AllowMultiple = true)]
    public sealed class GenerateMethodSerializersAttribute : Attribute
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="GenerateMethodSerializersAttribute"/> class.
        /// </summary>
        /// <param name="proxyBase">The proxy base type.</param>
        /// <param name="isExtension">if set to <see langword="true"/>, this is an extension interface.</param>
        public GenerateMethodSerializersAttribute(Type proxyBase, bool isExtension = false)
        {
            ProxyBase = proxyBase;
            IsExtension = isExtension;
        }

        /// <summary>
        /// Gets the base type which the source generator will use for generated proxy classes.
        /// </summary>
        /// <value>The proxy base type.</value>
        public Type ProxyBase { get; }

        /// <summary>
        /// Gets a value indicating whether this interface is an extension interface.
        /// </summary>
        /// <value>true if this instance is extension; otherwise, false.</value>
        public bool IsExtension { get; }

        /// <summary>
        /// Gets or sets the base type for the method call representation which is generated for any method which returns a <see cref="System.Threading.Tasks.ValueTask"/>.
        /// </summary>
        public Type ValueTaskInvoker { get; init; }

        /// <summary>
        /// Gets or sets the base type for the method call representation which is generated for any method which returns a <see cref="System.Threading.Tasks.ValueTask{T}"/> value.
        /// </summary>
        public Type ValueTask1Invoker { get; init; }

        /// <summary>
        /// Gets or sets the base type for the method call representation which is generated for any method which returns a <see cref="System.Threading.Tasks.Task"/> value.
        /// </summary>
        public Type TaskInvoker { get; init; }

        /// <summary>
        /// Gets or sets the base type for the method call representation which is generated for any method which returns a <see cref="System.Threading.Tasks.Task{T}"/> value.
        /// </summary>
        public Type Task1Invoker { get; init; }

        /// <summary>
        /// Gets or sets the base type for the method call representation which is generated for any method which returns <see cref="void"/>.
        /// </summary>
        public Type VoidInvoker { get; init; }
    }

    /// <summary>
    /// Applied to method attributes on invokable interfaces to specify the name of the method on the base type to call when submitting a request.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class InvokeMethodNameAttribute : Attribute
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="InvokeMethodNameAttribute"/> class.
        /// </summary>
        /// <param name="invokeMethodName">Name of the invoke method.</param>
        public InvokeMethodNameAttribute(string invokeMethodName)
        {
            InvokeMethodName = invokeMethodName;
        }

        /// <summary>
        /// Gets the name of the invoke method.
        /// </summary>
        /// <value>The name of the invoke method.</value>
        public string InvokeMethodName { get; }
    }

    /// <summary>
    /// Applied to proxy base types and to attribute types used on invokable interface methods to specify the base type for the invokable object which represents a method call.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
    public sealed class DefaultInvokeMethodNameAttribute : Attribute
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="DefaultInvokeMethodNameAttribute"/> class.
        /// </summary>
        /// <param name="returnType">Type of the return.</param>
        /// <param name="methodName">Name of the method.</param>
        public DefaultInvokeMethodNameAttribute(Type returnType, string methodName)
        {
            ReturnType = returnType;
            MethodName = methodName;
        }

        /// <summary>
        /// Gets the interface method return type which this attribute applies to.
        /// </summary>
        public Type ReturnType { get; }

        /// <summary>
        /// Gets the name of the method on the proxy base type to call when methods are invoked.
        /// </summary>
        public string MethodName { get; }
    }

    /// <summary>
    /// Applied to interface method attribute types to specify a method to be called on invokable objects which are created when invoking that interface method.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
    public sealed class InvokableCustomInitializerAttribute : Attribute
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="InvokableCustomInitializerAttribute"/> class.
        /// </summary>
        /// <param name="methodName">Name of the method.</param>
        /// <param name="methodArgumentValue">The method argument value.</param>
        public InvokableCustomInitializerAttribute(string methodName, object methodArgumentValue)
        {
            MethodName = methodName;
            MethodArgumentValue = methodArgumentValue;
            AttributeArgumentIndex = -1;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="InvokableCustomInitializerAttribute"/> class.
        /// </summary>
        /// <param name="methodName">Name of the method.</param>
        public InvokableCustomInitializerAttribute(string methodName)
        {
            MethodName = methodName;
            MethodArgumentValue = null;
            AttributeArgumentIndex = 0;
        }

        /// <summary>
        /// Gets the name of the method.
        /// </summary>
        public string MethodName { get; }

        /// <summary>
        /// Gets or sets the index of the attribute argument to propagate to the custom initializer method.
        /// </summary>
        public int AttributeArgumentIndex { get; init; }

        /// <summary>
        /// Gets or sets the name of the attribute argument to propagate to the custom initializer method.
        /// </summary>
        public int AttributeArgumentName { get; init; }

        /// <summary>
        /// Gets or sets the value to pass to the custom initializer method.
        /// </summary>
        public object MethodArgumentValue { get; }
    }

    /// <summary>
    /// Applied to proxy base types and to attribute types used on invokable interface methods to specify the base type for the invokable object which represents a method call.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
    public sealed class DefaultInvokableBaseTypeAttribute : Attribute
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="DefaultInvokableBaseTypeAttribute"/> class.
        /// </summary>
        /// <param name="returnType">The return type of methods which this attribute applies to.</param>
        /// <param name="invokableBaseType">Type of the invokable base.</param>
        public DefaultInvokableBaseTypeAttribute(Type returnType, Type invokableBaseType)
        {
            ReturnType = returnType;
            InvokableBaseType = invokableBaseType;
        }

        /// <summary>
        /// Gets the return type of methods which this attribute applies to.
        /// </summary>
        public Type ReturnType { get; }

        /// <summary>
        /// Gets the base type for the invokable object class which will be used to represent method calls to methods which this attribute applies to.
        /// </summary>
        public Type InvokableBaseType { get; }

        /// <summary>
        /// Gets or sets the name of the method on the proxy object which is used to invoke this method.
        /// </summary>
        public string ProxyInvokeMethodName { get; init; }
    }

    /// <summary>
    /// Applied to attribute types used on invokable interface methods to specify the base type for the invokable object which represents a method call.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface, AllowMultiple = true)]
    public sealed class InvokableBaseTypeAttribute : Attribute
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="InvokableBaseTypeAttribute"/> class.
        /// </summary>
        /// <param name="proxyBaseClass">The proxy base class.</param>
        /// <param name="returnType">The return type of methods which this attribute applies to.</param>
        /// <param name="invokableBaseType">Type of the invokable base.</param>
        public InvokableBaseTypeAttribute(Type proxyBaseClass, Type returnType, Type invokableBaseType)
        {
            ProxyBaseClass = proxyBaseClass;
            ReturnType = returnType;
            InvokableBaseType = invokableBaseType;
        }

        /// <summary>
        /// Gets the proxy base class which this attribute applies to.
        /// </summary>
        public Type ProxyBaseClass { get; }

        /// <summary>
        /// Gets the method return type which this attribute applies to.
        /// </summary>
        public Type ReturnType { get; }

        /// <summary>
        /// Gets the base type to use for the generated invokable object.
        /// </summary>
        public Type InvokableBaseType { get; }

        /// <summary>
        /// Gets or sets the name of the method on the proxy object to invoke when this method is called.
        /// </summary>
        public string ProxyInvokeMethodName { get; init; }
    }

    /// <summary>
    /// Applied to method attributes on invokable interfaces to specify the name of the method to call to get a completion source which is submitted to the submit method and eventually returned to the caller.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class GetCompletionSourceMethodNameAttribute : Attribute
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="GetCompletionSourceMethodNameAttribute"/> class.
        /// </summary>
        /// <param name="methodName">The name of the method used to get a completion source for requests submitted to the runtime.</param>
        public GetCompletionSourceMethodNameAttribute(string methodName)
        {
            MethodName = methodName;
        }

        /// <summary>
        /// Gets the name of the method used to get a completion source for requests submitted to the runtime.
        /// </summary>
        public string MethodName { get; }
    }

    /// <summary>
    /// Specifies the unique identity of a member.
    /// </summary>
    /// <remarks>
    /// Every serializable member in a type which has <see cref="GenerateSerializerAttribute"/> applied to it must have one <see cref="IdAttribute"/> attribute applied with a unique <see cref="IdAttribute.Id"/> value.
    /// </remarks>
    /// <seealso cref="System.Attribute" />
    [AttributeUsage(
        AttributeTargets.Field
        | AttributeTargets.Property
        | AttributeTargets.Class
        | AttributeTargets.Struct
        | AttributeTargets.Enum
        | AttributeTargets.Method)]
    public sealed class IdAttribute : Attribute
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="IdAttribute"/> class.
        /// </summary>
        /// <param name="id">The identifier.</param>
        public IdAttribute(ushort id)
        {
            Id = id;
        }

        /// <summary>
        /// Gets the identifier.
        /// </summary>
        /// <value>The identifier.</value>
        public ushort Id { get; }
    }

    /// <summary>
    /// When applied to a type or method, indicates that a well-known numeric identifier can be used to uniquely identify that type or method.
    /// </summary>
    /// <remarks>
    /// In the case of a type, the numeric id must be globally unique. In the case of a method, the numeric id must be unique to the declaring type.
    /// </remarks>
    /// <seealso cref="System.Attribute" />
    [AttributeUsage(
        AttributeTargets.Class
        | AttributeTargets.Interface
        | AttributeTargets.Struct
        | AttributeTargets.Enum
        | AttributeTargets.Method)]
    public sealed class WellKnownIdAttribute : Attribute
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="WellKnownIdAttribute"/> class.
        /// </summary>
        /// <param name="id">The identifier.</param>
        /// <remarks>
        /// In the case of a type, the numeric id must be globally unique. In the case of a method, the numeric id must be unique to the declaring type.
        /// </remarks>
        public WellKnownIdAttribute(uint id)
        {
            Id = id;
        }

        /// <summary>
        /// Gets the identifier.
        /// </summary>
        /// <remarks>
        /// In the case of a type, the numeric id must be globally unique. In the case of a method, the numeric id must be unique to the declaring type.
        /// </remarks>
        public uint Id { get; }
    }

    /// <summary>
    /// When applied to a type or method, specifies a well-known name which can be used to identify that type or method.
    /// </summary>
    /// <remarks>
    /// In the case of a type, the alias must be globally unique. In the case of a method, the alias must be unique to the declaring type.
    /// </remarks>
    /// <seealso cref="System.Attribute" />
    [AttributeUsage(
        AttributeTargets.Class
        | AttributeTargets.Interface
        | AttributeTargets.Struct
        | AttributeTargets.Enum
        | AttributeTargets.Method)]
    public sealed class WellKnownAliasAttribute : Attribute
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="WellKnownAliasAttribute"/> class.
        /// </summary>
        /// <param name="alias">The alias.</param>
        /// <remarks>
        /// In the case of a type, the alias must be globally unique. In the case of a method, the alias must be unique to the declaring type.
        /// </remarks>
        public WellKnownAliasAttribute(string alias)
        {
            Alias = alias;
        }

        /// <summary>
        /// Gets the alias.
        /// </summary>
        /// <remarks>
        /// In the case of a type, the alias must be globally unique. In the case of a method, the alias must be unique to the declaring type.
        /// </remarks>
        public string Alias { get; }
    }

    /// <summary>
    /// When applied to a type, indicates that the type is a serializer and that it should be automatically registered.
    /// </summary>
    /// <seealso cref="System.Attribute" />
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
    public sealed class RegisterSerializerAttribute : Attribute
    {
    }

    /// <summary>
    /// When applied to a type, indicates that the type is an activator and that it should be automatically registered.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
    public sealed class RegisterActivatorAttribute : Attribute
    {
    }

    /// <summary>
    /// When applied to a type, indicates that the type is an copier and that it should be automatically registered.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
    public sealed class RegisterCopierAttribute : Attribute
    {
    }

    /// <summary>
    /// When applied to a type, indicates that the type should be activated using a registered activator rather than via its constructor or another mechanism.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
    public sealed class UseActivatorAttribute : Attribute
    {
    }

    /// <summary>
    /// When applied to a type, indicates that generated serializers for the type should not track references to the type.
    /// </summary>
    /// <remarks>
    /// Reference tracking allows a serializable type to participate in a cyclic object graph.
    /// </remarks>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
    public sealed class SuppressReferenceTrackingAttribute : Attribute
    {
    }

    /// <summary>
    /// When applied to a type, indicates that generated serializers for the type should avoid serializing members if the member value is equal to its default value.
    /// </summary>
    /// <seealso cref="System.Attribute" />
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
    public sealed class OmitDefaultMemberValuesAttribute : Attribute
    {
    }

    /// <summary>
    /// Indicates that the type, type member, parameter, or return value which it is applied to should be treated as immutable and therefore that defensive copies are never required.
    /// </summary>
    /// <seealso cref="System.Attribute" />
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.Parameter | AttributeTargets.ReturnValue)]
    public sealed class ImmutableAttribute : Attribute
    {
    }

    /// <summary>
    /// Specifies an assembly to be added as an application part.
    /// </summary>
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    public sealed class ApplicationPartAttribute : Attribute
    {
        /// <summary>
        /// Initializes a new instance of <see cref="ApplicationPartAttribute" />.
        /// </summary>
        /// <param name="assemblyName">The assembly name.</param>
        public ApplicationPartAttribute(string assemblyName)
        {
            AssemblyName = assemblyName ?? throw new ArgumentNullException(nameof(assemblyName));
        }

        /// <summary>
        /// Gets the assembly name.
        /// </summary>
        public string AssemblyName { get; }
    }

    /// <summary>
    /// Specifies a type to be instantiated and invoked when performing serialization operations on instances of the type which this attribute is attached to.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
    public sealed class SerializationCallbacksAttribute : Attribute
    {
        /// <summary>
        /// Instantiates a new <see cref="SerializationCallbacksAttribute"/> instance.
        /// </summary>
        /// <param name="hookType">The type of the object used to invoke serialization hooks.</param>
        public SerializationCallbacksAttribute(Type hookType)
        {
            HookType = hookType;
        }

        /// <summary>
        /// The type of the hook class.
        /// </summary>
        /// <remarks>
        /// This value is used to get the hooks implementation from an <see cref="IServiceProvider"/>.
        /// </remarks>
        public Type HookType { get; }
    }

    /// <summary>
    /// When applied to a constructor, indicates that generated activator implementations should use that constructor when activating instances.
    /// </summary>
    /// <remarks>
    /// This attribute can be used to call constructors which require injected dependencies.
    /// </remarks>
    [AttributeUsage(AttributeTargets.Constructor)]
    public sealed class GeneratedActivatorConstructorAttribute : Attribute
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="GeneratedActivatorConstructorAttribute"/> class.
        /// </summary>
        public GeneratedActivatorConstructorAttribute()
        {
        }
    }

    /// <summary>
    /// Indicates that the source generator should also inspect and generate code for the assembly containing the specified type.
    /// </summary>
    /// <seealso cref="System.Attribute" />
    [AttributeUsage(AttributeTargets.Assembly)]
    public sealed class GenerateCodeForDeclaringAssemblyAttribute : Attribute
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="GenerateCodeForDeclaringAssemblyAttribute"/> class.
        /// </summary>
        /// <param name="type">Any type in the assembly which the source generator should inspect and generate source for.</param>
        public GenerateCodeForDeclaringAssemblyAttribute(Type type)
        {
            Type = type;
        }

        /// <summary>
        /// Gets a type in the assembly which the source generator should inspect and generate source for.
        /// </summary>
        /// <value>The type.</value>
        public Type Type { get; }
    }
}