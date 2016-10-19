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
        /// A reference to the <see cref="SerializationContext.Current"/> getter method.
        /// </summary>
        public readonly MethodInfo GetCurrentSerializationContext;

        /// <summary>
        /// A reference to the <see cref="SerializationContext.RecordObject(object, object)"/> method.
        /// </summary>
        public readonly MethodInfo RecordObjectWhileCopying;

        /// <summary>
        /// A reference to <see cref="SerializationManager.DeepCopyInner"/>
        /// </summary>
        public readonly MethodInfo DeepCopyInner;

        /// <summary>
        /// A reference to the <see cref="SerializationManager.SerializeInner(object, BinaryTokenStreamWriter, Type)"/> method.
        /// </summary>
        public readonly MethodInfo SerializeInner;

        /// <summary>
        /// A reference to the <see cref="SerializationManager.DeserializeInner(Type, BinaryTokenStreamReader)"/> method.
        /// </summary>
        public readonly MethodInfo DeserializeInner;

        /// <summary>
        /// A reference to the <see cref="DeserializationContext.RecordObject(object)"/> method.
        /// </summary>
        public readonly MethodInfo RecordObjectWhileDeserializing;

        /// <summary>
        /// A reference to the getter for <see cref="DeserializationContext.Current"/>.
        /// </summary>
        public readonly MethodInfo GetCurrentDeserializationContext;

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
            this.GetTypeFromHandle = TypeUtils.Method(() => Type.GetTypeFromHandle(typeof(int).TypeHandle));
            this.DeepCopyInner = TypeUtils.Method(() => SerializationManager.DeepCopyInner(typeof(int)));
            this.SerializeInner = TypeUtils.Method(() => SerializationManager.SerializeInner(default(object), default(BinaryTokenStreamWriter), default(Type)));
            this.DeserializeInner = TypeUtils.Method(() => SerializationManager.DeserializeInner(default(Type), default(BinaryTokenStreamReader)));

            this.GetCurrentSerializationContext = TypeUtils.Property((object _) => SerializationContext.Current).GetMethod;
            this.RecordObjectWhileCopying = TypeUtils.Method((SerializationContext ctx) => ctx.RecordObject(default(object), default(object)));

            this.GetCurrentDeserializationContext = TypeUtils.Property((object _) => DeserializationContext.Current).GetMethod;
            this.RecordObjectWhileDeserializing = TypeUtils.Method((DeserializationContext ctx) => ctx.RecordObject(default(object)));
            this.SerializerDelegate =
                TypeUtils.Method((SerializationManager.Serializer del) => del.Invoke(default(object), default(BinaryTokenStreamWriter), default(Type)));
            this.DeserializerDelegate = TypeUtils.Method((SerializationManager.Deserializer del) => del.Invoke(default(Type), default(BinaryTokenStreamReader)));
            this.DeepCopierDelegate = TypeUtils.Method((SerializationManager.DeepCopier del) => del.Invoke(default(object)));
        }
    }
}