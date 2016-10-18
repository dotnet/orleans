namespace Orleans.Serialization
{
    using System;
    using System.Reflection;
    using System.Reflection.Emit;

    internal class IlDelegateBuilder<TDelegate>
        where TDelegate : class
    {
        private readonly DynamicMethod dynamicMethod;

        private readonly ILGenerator il;

        private readonly ReflectedSerializationMethodInfo methods;

        /// <summary>
        /// Creates a new instance of the <see cref="IlDelegateBuilder{TDelegate}"/> class.
        /// </summary>
        /// <param name="name">The name of the new delegate.</param>
        /// <param name="methods">The reflected methods used during delegate creation.</param>
        /// <param name="methodInfo">
        /// The method info for <typeparamref name="TDelegate"/> delegates, used for determining parameter types.
        /// </param>
        public IlDelegateBuilder(string name, ReflectedSerializationMethodInfo methods, MethodInfo methodInfo)
        {
            this.methods = methods;
            var returnType = methodInfo.ReturnType;
            var parameterTypes = GetParameterTypes(methodInfo);
            this.dynamicMethod = new DynamicMethod(name, returnType, parameterTypes, typeof(IlDelegateBuilder<>).GetTypeInfo().Module, true);
            this.il = this.dynamicMethod.GetILGenerator();
        }

        /// <summary>
        /// Declares a local variable with the specified type.
        /// </summary>
        /// <param name="type">The type.</param>
        /// <returns>The newly declared local.</returns>
        public Local DeclareLocal(Type type) => new IlGeneratorLocal(this.il.DeclareLocal(type));

        /// <summary>
        /// Loads the argument at the given index onto the stack.
        /// </summary>
        /// <param name="index">
        /// The index of the argument to load.
        /// </param>
        public void LoadArgument(ushort index) => this.il.Emit(OpCodes.Ldarg, index);

        /// <summary>
        /// Pops the stack and stores it in the specified local.
        /// </summary>
        /// <param name="local">The local variable to store into.</param>
        public void StoreLocal(Local local) => this.il.Emit(OpCodes.Stloc, (IlGeneratorLocal)local);

        /// <summary>
        /// Pushes the specified local onto the stack.
        /// </summary>
        /// <param name="local">The local variable to load from.</param>
        public void LoadLocal(Local local) => this.il.Emit(OpCodes.Ldloc, (IlGeneratorLocal)local);

        /// <summary>
        /// Loads the specified field onto the stack from the referenced popped from the stack.
        /// </summary>
        /// <param name="field">The field.</param>
        public void LoadField(FieldInfo field) => this.il.Emit(OpCodes.Ldfld, field);

        /// <summary>
        /// Boxes the value on the top of the stack.
        /// </summary>
        /// <param name="type">The value type.</param>
        public void Box(Type type) => this.il.Emit(OpCodes.Box, type);

        /// <summary>
        /// Loads the specified type and pushes it onto the stack.
        /// </summary>
        /// <param name="type">The type to load.</param>
        public void LoadType(Type type)
        {
            this.il.Emit(OpCodes.Ldtoken, type);
            this.Call(this.methods.GetTypeFromHandle);
        }

        /// <summary>
        /// Calls the specified method.
        /// </summary>
        /// <param name="method">The method to call.</param>
        public void Call(MethodInfo method) => this.il.Emit(OpCodes.Call, method);

        /// <summary>
        /// Returns from the current method.
        /// </summary>
        public void Return() => this.il.Emit(OpCodes.Ret);

        /// <summary>
        /// Pops the value on the top of the stack and stores it in the specified field on the object popped from the top of the stack.
        /// </summary>
        /// <param name="field">The field to store into.</param>
        public void StoreField(FieldInfo field) => this.il.Emit(OpCodes.Stfld, field);

        /// <summary>
        /// Pushes the address of the specified local onto the stack.
        /// </summary>
        /// <param name="local">The local variable.</param>
        public void LoadLocalAddress(Local local) => this.il.Emit(OpCodes.Ldloca, (IlGeneratorLocal)local);

        /// <summary>
        /// Unboxes the value on the top of the stack.
        /// </summary>
        /// <param name="type">The value type.</param>
        public void UnboxAny(Type type) => this.il.Emit(OpCodes.Unbox_Any, type);

        /// <summary>
        /// Casts the object on the top of the stack to the specified type.
        /// </summary>
        /// <param name="type">The type.</param>
        public void CastClass(Type type) => this.il.Emit(OpCodes.Castclass, type);

        /// <summary>
        /// Initializes the value type on the stack, setting all fields to their default value.
        /// </summary>
        /// <param name="type">The value type.</param>
        public void InitObject(Type type) => this.il.Emit(OpCodes.Initobj, type);

        /// <summary>
        /// Constructs a new instance of the object with the specified constructor.
        /// </summary>
        /// <param name="constructor">The constructor to call.</param>
        public void NewObject(ConstructorInfo constructor)
        {
            this.il.Emit(OpCodes.Newobj, constructor);
        }

        /// <summary>
        /// Builds a delegate from the previously emitted instructions.
        /// </summary>
        /// <returns>The delegate.</returns>
        public TDelegate Build() => this.dynamicMethod.CreateDelegate(typeof(TDelegate)) as TDelegate;

        /// <summary>
        /// Pushes the specified local variable as a reference onto the stack.
        /// </summary>
        /// <param name="type">The type.</param>
        /// <param name="local">The local.</param>
        public void LoadLocalAsReference(Type type, Local local)
        {
            if (type.GetTypeInfo().IsValueType)
            {
                this.LoadLocalAddress(local);
            }
            else
            {
                this.LoadLocal(local);
            }
        }

        /// <summary>
        /// Boxes the value on the top of the stack if it's a value type.
        /// </summary>
        /// <param name="type">The type.</param>
        public void BoxIfValueType(Type type)
        {
            if (type.GetTypeInfo().IsValueType)
            {
                this.Box(type);
            }
        }

        /// <summary>
        /// Casts or unboxes the value at the top of the stack into the specified type.
        /// </summary>
        /// <param name="type">The type.</param>
        public void CastOrUnbox(Type type)
        {
            if (type.GetTypeInfo().IsValueType)
            {
                this.UnboxAny(type);
            }
            else
            {
                this.CastClass(type);
            }
        }

        /// <summary>
        /// Creates a new instance of the specified type and stores it in the specified local.
        /// </summary>
        /// <param name="type">The type.</param>
        /// <param name="local">The local.</param>
        public void CreateInstance(Type type, Local local)
        {
            var constructorInfo = type.GetConstructor(Type.EmptyTypes);
            if (type.GetTypeInfo().IsValueType)
            {
                this.LoadLocalAddress(local);
                this.InitObject(type);
            }
            else if (constructorInfo != null)
            {
                // Use the default constructor.
                this.NewObject(constructorInfo);
                this.StoreLocal(local);
            }
            else
            {
                this.LoadType(type);
                this.Call(this.methods.GetUninitializedObject);
                this.CastClass(type);
                this.StoreLocal(local);
            }
        }

        private static Type[] GetParameterTypes(MethodInfo method)
        {
            var parameters = method.GetParameters();
            var result = new Type[parameters.Length];
            for (var i = 0; i < parameters.Length; ++i)
            {
                result[i] = parameters[i].ParameterType;
            }

            return result;
        }
        
        /// <summary>
        /// Represents a local variable created via a call to <see cref="DeclareLocal"/>.
        /// </summary>
        internal abstract class Local
        {
        }

        private class IlGeneratorLocal : Local
        {
            private readonly LocalBuilder value;

            public IlGeneratorLocal(LocalBuilder value)
            {
                this.value = value;
            }

            public static implicit operator LocalBuilder(IlGeneratorLocal local) => local.value;
        }
    }
}