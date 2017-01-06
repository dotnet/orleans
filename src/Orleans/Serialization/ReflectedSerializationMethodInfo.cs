namespace Orleans.Serialization
{
    using System;
    using System.Reflection;
    using System.Runtime.Serialization;

    using Orleans.Runtime;

    /// <summary>
    /// Holds references to methods which are used during serialization.
    /// </summary>
    internal class ReflectedSerializationMethodInfo
    {
        /// <summary>
        /// A reference to the <see cref="SerializationContext.StreamWriter"/> getter.
        /// </summary>
        public readonly MethodInfo GetStreamFromSerializationContext;

        /// <summary>
        /// A reference to the getter for <see cref="SerializationManager.CurrentDeserializationContext"/>.
        /// </summary>
        public readonly MethodInfo GetStreamFromDeserializationContext;

        /// <summary>
        /// A reference to the <see cref="SerializationContext.RecordCopy"/> method.
        /// </summary>
        public readonly MethodInfo RecordObjectWhileCopying;

        /// <summary>
        /// A reference to <see cref="SerializationManager.DeepCopyInner"/>
        /// </summary>
        public readonly MethodInfo DeepCopyInner;

        /// <summary>
        /// A reference to the <see cref="SerializationManager.SerializeInner(object, ISerializationContext, Type)"/> method.
        /// </summary>
        public readonly MethodInfo SerializeInner;

        /// <summary>
        /// A reference to the <see cref="SerializationManager.DeserializeInner(Type, IDeserializationContext)"/> method.
        /// </summary>
        public readonly MethodInfo DeserializeInner;

        /// <summary>
        /// A reference to the <see cref="IDeserializationContext.RecordObject(object)"/> method.
        /// </summary>
        public readonly MethodInfo RecordObjectWhileDeserializing;

        /// <summary>
        /// A reference to a method which returns an uninitialized object of the provided type.
        /// </summary>
        public readonly MethodInfo GetUninitializedObject;

        /// <summary>
        /// A reference to <see cref="Type.GetTypeFromHandle"/>.
        /// </summary>
        public readonly MethodInfo GetTypeFromHandle;

        /// <summary>
        /// The <see cref="MethodInfo"/> for the <see cref="SerializationManager.Serializer"/> delegate.
        /// </summary>
        public readonly MethodInfo SerializerDelegate;

        /// <summary>
        /// The <see cref="MethodInfo"/> for the <see cref="SerializationManager.Deserializer"/> delegate.
        /// </summary>
        public readonly MethodInfo DeserializerDelegate;

        /// <summary>
        /// The <see cref="MethodInfo"/> for the <see cref="SerializationManager.DeepCopier"/> delegate.
        /// </summary>
        public readonly MethodInfo DeepCopierDelegate;

        public ReflectedSerializationMethodInfo()
        {
#if NETSTANDARD
            this.GetUninitializedObject = TypeUtils.Method(() => SerializationManager.GetUninitializedObjectWithFormatterServices(typeof(int)));
#else
            this.GetUninitializedObject = TypeUtils.Method(() => FormatterServices.GetUninitializedObject(typeof(int)));
#endif
            this.GetTypeFromHandle = TypeUtils.Method(() => Type.GetTypeFromHandle(typeof(Type).TypeHandle));
            this.DeepCopyInner = TypeUtils.Method(() => SerializationManager.DeepCopyInner(default(Type), default(ICopyContext)));
            this.SerializeInner = TypeUtils.Method(() => SerializationManager.SerializeInner(default(object), default(ISerializationContext), default(Type)));
            this.DeserializeInner = TypeUtils.Method(() => SerializationManager.DeserializeInner(default(Type), default(IDeserializationContext)));
            
            this.RecordObjectWhileCopying = TypeUtils.Method((ICopyContext ctx) => ctx.RecordCopy(default(object), default(object)));

            this.GetStreamFromDeserializationContext = TypeUtils.Property((IDeserializationContext ctx) => ctx.StreamReader).GetMethod;
            this.GetStreamFromSerializationContext = TypeUtils.Property((ISerializationContext ctx) => ctx.StreamWriter).GetMethod;

            this.RecordObjectWhileDeserializing = TypeUtils.Method((IDeserializationContext ctx) => ctx.RecordObject(default(object)));
            this.SerializerDelegate =
                TypeUtils.Method((SerializationManager.Serializer del) => del.Invoke(default(object), default(ISerializationContext), default(Type)));
            this.DeserializerDelegate = TypeUtils.Method((SerializationManager.Deserializer del) => del.Invoke(default(Type), default(IDeserializationContext)));
            this.DeepCopierDelegate = TypeUtils.Method((SerializationManager.DeepCopier del) => del.Invoke(default(object), default(ICopyContext)));
        }
    }
}