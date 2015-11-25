/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

using Orleans.Runtime;
using Orleans.Concurrency;

namespace Orleans.CodeGeneration
{
    internal static class GrainInterfaceData
    {
        [Serializable]
        internal class RulesViolationException : ArgumentException
        {
            public RulesViolationException(string message, List<string> violations)
                : base(message)
            {
                Violations = violations;
            }

            public List<string> Violations { get; private set; }
        }

        public static bool IsGrainInterface(Type t)
        {
            if (t.GetTypeInfo().IsClass)
                return false;
            if (t == typeof(IGrainObserver) || t == typeof(IAddressable) || t == typeof(IGrainExtension))
                return false;
            if (t == typeof(IGrain) || t == typeof(IGrainWithGuidKey) || t == typeof(IGrainWithIntegerKey)
                || t == typeof(IGrainWithGuidCompoundKey) || t == typeof(IGrainWithIntegerCompoundKey))
                return false;
            if (t == typeof (ISystemTarget))
                return false;

            return typeof (IAddressable).GetTypeInfo().IsAssignableFrom(t);
        }

        public static MethodInfo[] GetMethods(Type grainType, bool bAllMethods = true)
        {
            var methodInfos = new List<MethodInfo>();
            GetMethodsImpl(grainType, grainType, methodInfos);
            var flags = BindingFlags.Public | BindingFlags.InvokeMethod | BindingFlags.Instance;
            if (!bAllMethods)
                flags |= BindingFlags.DeclaredOnly;

            MethodInfo[] infos = grainType.GetMethods(flags);
            IEqualityComparer<MethodInfo> methodComparer = new MethodInfoComparer();
            foreach (var methodInfo in infos)
                if (!methodInfos.Contains(methodInfo, methodComparer))
                    methodInfos.Add(methodInfo);

            return methodInfos.ToArray();
        }

        public static string GetParameterName(ParameterInfo info)
        {
            var n = info.Name;
            return string.IsNullOrEmpty(n) ? "arg" + info.Position : n;
        }

        public static bool IsTaskType(Type t)
        {
            var typeInfo = t.GetTypeInfo();
            return t == typeof (Task)
                || (typeInfo.IsGenericType && typeInfo.GetGenericTypeDefinition().FullName == "System.Threading.Tasks.Task`1");
        }

        /// <summary>
        /// Whether method is read-only, i.e. does not modify grain state, 
        /// a method marked with [ReadOnly].
        /// </summary>
        /// <param name="info"></param>
        /// <returns></returns>
        public static bool IsReadOnly(MethodInfo info)
        {
            return info.GetCustomAttributes(typeof (ReadOnlyAttribute), true).Length > 0;
        }

        public static bool IsAlwaysInterleave(MethodInfo methodInfo)
        {
            return methodInfo.GetCustomAttributes(typeof (AlwaysInterleaveAttribute), true).Length > 0;
        }

        public static bool IsUnordered(MethodInfo methodInfo)
        {
            return methodInfo.DeclaringType.GetCustomAttributes(typeof (UnorderedAttribute), true).Length > 0 ||
                (methodInfo.DeclaringType.GetInterfaces().Any(i => i.GetCustomAttributes(typeof (UnorderedAttribute), true)
                    .Length > 0 && methodInfo.DeclaringType.GetInterfaceMap(i)
                    .TargetMethods.Contains(methodInfo))) || IsStatelessWorker(methodInfo);
        }

        public static bool IsStatelessWorker(Type grainType)
        {
            return grainType.GetCustomAttributes(typeof (StatelessWorkerAttribute), true).Length > 0 ||
                grainType.GetInterfaces()
                    .Any(i => i.GetCustomAttributes(typeof (StatelessWorkerAttribute), true).Length > 0);
        }

        public static bool IsStatelessWorker(MethodInfo methodInfo)
        {
            return methodInfo.DeclaringType.GetCustomAttributes(typeof (StatelessWorkerAttribute), true).Length > 0 ||
                (methodInfo.DeclaringType.GetInterfaces().Any(i => i.GetCustomAttributes(
                    typeof (StatelessWorkerAttribute), true).Length > 0 &&
                    methodInfo.DeclaringType.GetInterfaceMap(i).TargetMethods.Contains(methodInfo)));
        }

        public static Dictionary<int, Type> GetRemoteInterfaces(Type type, bool checkIsGrainInterface = true)
        {
            var dict = new Dictionary<int, Type>();

            if (IsGrainInterface(type))
                dict.Add(ComputeInterfaceId(type), type);

            Type[] interfaces = type.GetInterfaces();
            foreach (Type interfaceType in interfaces.Where(i => !checkIsGrainInterface || IsGrainInterface(i)))
                dict.Add(ComputeInterfaceId(interfaceType), interfaceType);

            return dict;
        }
        
        public static int ComputeMethodId(MethodInfo methodInfo)
        {
            var strMethodId = new StringBuilder(methodInfo.Name + "(");
            ParameterInfo[] parameters = methodInfo.GetParameters();
            bool bFirstTime = true;
            foreach (ParameterInfo info in parameters)
            {
                if (!bFirstTime)
                    strMethodId.Append(",");

                strMethodId.Append(info.ParameterType.Name);
                var typeInfo = info.ParameterType.GetTypeInfo();
                if (typeInfo.IsGenericType)
                {
                    Type[] args = typeInfo.GetGenericArguments();
                    foreach (Type arg in args)
                        strMethodId.Append(arg.Name);
                }
                bFirstTime = false;
            }
            strMethodId.Append(")");
            return Utils.CalculateIdHash(strMethodId.ToString());
        }

        public static int GetGrainInterfaceId(Type grainInterface)
        {
            return GetTypeCode(grainInterface);
        }

        public static bool IsTaskBasedInterface(Type type)
        {
            var methods = type.GetMethods();
            // An interface is task-based if it has at least one method that returns a Task or at least one parent that's task-based.
            return methods.Any(m => IsTaskType(m.ReturnType)) || type.GetInterfaces().Any(IsTaskBasedInterface);
        }

        public static bool IsGrainType(Type grainType)
        {
            return typeof (IGrain).IsAssignableFrom(grainType);
        }

        public static int ComputeInterfaceId(Type interfaceType)
        {
            var ifaceName = TypeUtils.GetFullName(interfaceType);
            var ifaceId = Utils.CalculateIdHash(ifaceName);
            return ifaceId;
        }

        public static int GetGrainClassTypeCode(Type grainClass)
        {
            return GetTypeCode(grainClass);
        }

        
        internal static bool TryValidateInterfaceRules(Type type, out List<string> violations)
        {
            violations = new List<string>();

            bool success = ValidateInterfaceMethods(type, violations);
            return success && ValidateInterfaceProperties(type, violations);
        }

        internal static void ValidateInterfaceRules(Type type)
        {
            List<string> violations;
            if (!TryValidateInterfaceRules(type, out violations))
            {
                foreach (var violation in violations)
                    ConsoleText.WriteLine("ERROR: " + violation);

                throw new RulesViolationException(
                    string.Format("{0} does not conform to the grain interface rules.", type.FullName), violations);
            }
        }

        internal static void ValidateInterface(Type type)
        {
            if (!IsGrainInterface(type))
                throw new ArgumentException(String.Format("{0} is not a grain interface", type.FullName));

            ValidateInterfaceRules(type);
        }

        private static bool ValidateInterfaceMethods(Type type, List<string> violations)
        {
            bool success = true;

            MethodInfo[] methods = type.GetMethods();
            foreach (MethodInfo method in methods)
            {
                if (method.IsSpecialName)
                    continue;

                if (IsPureObserverInterface(method.DeclaringType))
                {
                    if (method.ReturnType != typeof (void))
                    {
                        success = false;
                        violations.Add(String.Format("Method {0}.{1} must return void because it is defined within an observer interface.",
                            type.FullName, method.Name));
                    }
                }
                else if (!IsTaskType(method.ReturnType))
                {
                    success = false;
                    violations.Add(String.Format("Method {0}.{1} must return Task or Task<T> because it is defined within a grain interface.",
                        type.FullName, method.Name));
                }

                ParameterInfo[] parameters = method.GetParameters();
                foreach (ParameterInfo parameter in parameters)
                {
                    if (parameter.IsOut)
                    {
                        success = false;
                        violations.Add(String.Format("Argument {0} of method {1}.{2} is an output parameter. Output parameters are not allowed in grain interfaces.",
                            GetParameterName(parameter), type.FullName, method.Name));
                    }

                    if (parameter.ParameterType.GetTypeInfo().IsByRef)
                    {
                        success = false;
                        violations.Add(String.Format("Argument {0} of method {1}.{2} is an a reference parameter. Reference parameters are not allowed.",
                            GetParameterName(parameter), type.FullName, method.Name));
                    }
                }
            }

            return success;
        }

        private static bool ValidateInterfaceProperties(Type type, List<string> violations)
        {
            bool success = true;

            PropertyInfo[] properties = type.GetProperties();
            foreach (PropertyInfo property in properties)
            {
                success = false;
                violations.Add(String.Format("Properties are not allowed on grain interfaces:  {0}.{1}.",
                    type.FullName, property.Name));
            }

            return success;
        }

        /// <summary>
        /// decide whether the class is derived from Grain
        /// </summary>
        private static bool IsPureObserverInterface(Type t)
        {
            if (!typeof (IGrainObserver).IsAssignableFrom(t))
                return false;

            if (t == typeof (IGrainObserver))
                return true;

            if (t == typeof (IAddressable))
                return false;

            bool pure = false;
            foreach (Type iface in t.GetInterfaces())
            {
                if (iface == typeof (IAddressable)) // skip IAddressable that will be in the list regardless
                    continue;

                if (iface == typeof (IGrainExtension))
                    // Skip IGrainExtension, it's just a marker that can go on observer or grain interfaces
                    continue;

                pure = IsPureObserverInterface(iface);
                if (!pure)
                    return false;
            }

            return pure;
        }

        private class MethodInfoComparer : IEqualityComparer<MethodInfo>
        {
            #region IEqualityComparer<InterfaceInfo> Members

            public bool Equals(MethodInfo x, MethodInfo y)
            {
                var xString = new StringBuilder(x.Name);
                var yString = new StringBuilder(y.Name);

                ParameterInfo[] parms = x.GetParameters();
                foreach (ParameterInfo info in parms)
                {
                    var typeInfo = info.ParameterType.GetTypeInfo();
                    xString.Append(typeInfo.Name);
                    if (typeInfo.IsGenericType)
                    {
                        Type[] args = info.ParameterType.GetGenericArguments();
                        foreach (Type arg in args)
                            xString.Append(arg.Name);
                    }
                }

                parms = y.GetParameters();
                foreach (ParameterInfo info in parms)
                {
                    yString.Append(info.ParameterType.Name);
                    var typeInfo = info.ParameterType.GetTypeInfo();
                    if (typeInfo.IsGenericType)
                    {
                        Type[] args = info.ParameterType.GetGenericArguments();
                        foreach (Type arg in args)
                            yString.Append(arg.Name);
                    }
                }
                return String.CompareOrdinal(xString.ToString(), yString.ToString()) == 0;
            }

            public int GetHashCode(MethodInfo obj)
            {
                throw new NotImplementedException();
            }

            #endregion
        }

        /// <summary>
        /// Recurses through interface graph accumulating methods
        /// </summary>
        /// <param name="grainType">Grain type</param>
        /// <param name="serviceType">Service interface type</param>
        /// <param name="methodInfos">Accumulated </param>
        private static void GetMethodsImpl(Type grainType, Type serviceType, List<MethodInfo> methodInfos)
        {
            Type[] iTypes = GetRemoteInterfaces(serviceType, false).Values.ToArray();
            IEqualityComparer<MethodInfo> methodComparer = new MethodInfoComparer();

            var typeInfo = grainType.GetTypeInfo();

            foreach (Type iType in iTypes)
            {
                var mapping = new InterfaceMapping();
                
                if (typeInfo.IsClass)
                    mapping = grainType.GetInterfaceMap(iType);

                if (typeInfo.IsInterface || mapping.TargetType == grainType)
                {
                    foreach (var methodInfo in iType.GetMethods())
                    {
                        if (typeInfo.IsClass)
                        {
                            var mi = methodInfo;
                            var match = mapping.TargetMethods.Any(info => methodComparer.Equals(mi, info) &&
                                info.DeclaringType == grainType);

                            if (match)
                                if (!methodInfos.Contains(mi, methodComparer))
                                    methodInfos.Add(mi);
                        }
                        else if (!methodInfos.Contains(methodInfo, methodComparer))
                        {
                            methodInfos.Add(methodInfo);
                        }
                    }
                }
            }
        }

        private static int GetTypeCode(Type grainInterfaceOrClass)
        {
            var attrs = grainInterfaceOrClass.GetCustomAttributes(typeof(TypeCodeOverrideAttribute), false);
            var attr = attrs.Length > 0 ? attrs[0] as TypeCodeOverrideAttribute : null;
            var fullName = TypeUtils.GetTemplatedName(
                TypeUtils.GetFullName(grainInterfaceOrClass), 
                grainInterfaceOrClass, 
                grainInterfaceOrClass.GetGenericArguments(), 
                t => false);
            var typeCode = attr != null && attr.TypeCode > 0 ? attr.TypeCode : Utils.CalculateIdHash(fullName);
            return typeCode;
        }
    }
}