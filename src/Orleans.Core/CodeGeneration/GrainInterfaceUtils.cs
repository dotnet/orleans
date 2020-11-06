using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.Utilities;

namespace Orleans.CodeGeneration
{
    internal static class GrainInterfaceUtils
    {
        private static readonly MethodInfoComparer MethodComparer = new MethodInfoComparer();

        [Serializable]
        internal sealed class RulesViolationException : ArgumentException
        {
            public RulesViolationException(string message, List<string> violations)
                : base(message)
            {
                Violations = violations;
            }

            private RulesViolationException(SerializationInfo info, StreamingContext context) : base(info, context)
            {
                this.Violations = info.GetValue(nameof(Violations), typeof(List<string>)) as List<string>;
            }

            public override void GetObjectData(SerializationInfo info, StreamingContext context)
            {
                base.GetObjectData(info, context);
                info.AddValue(nameof(Violations), this.Violations);
            }

            public List<string> Violations { get; }
        }

        public static bool IsGrainInterface(Type t)
        {
            if (t.IsClass)
                return false;
            if (t == typeof(IGrainObserver) || t == typeof(IAddressable) || t == typeof(IGrainExtension))
                return false;
            if (t == typeof(IGrain) || t == typeof(IGrainWithGuidKey) || t == typeof(IGrainWithIntegerKey)
                || t == typeof(IGrainWithGuidCompoundKey) || t == typeof(IGrainWithIntegerCompoundKey))
                return false;
            if (t == typeof (ISystemTarget))
                return false;

            return typeof (IAddressable).IsAssignableFrom(t);
        }

        public static MethodInfo[] GetMethods(Type grainType, bool bAllMethods = true)
        {
            var methodInfos = new List<MethodInfo>();
            GetMethodsImpl(grainType, grainType, methodInfos);
            var flags = BindingFlags.Public | BindingFlags.Instance;
            if (!bAllMethods)
                flags |= BindingFlags.DeclaredOnly;

            MethodInfo[] infos = grainType.GetMethods(flags);
            foreach (var methodInfo in infos)
                if (!methodInfos.Contains(methodInfo, MethodComparer))
                    methodInfos.Add(methodInfo);

            return methodInfos.ToArray();
        }

        public static string GetParameterName(ParameterInfo info)
        {
            var n = info.Name;
            return string.IsNullOrEmpty(n) ? "arg" + info.Position.ToString() : n;
        }

        public static bool IsTaskType(Type t)
        {
            if (t == typeof(Task))
            {
                return true;
            }

            if (t.IsGenericType)
            {
                var def = t.GetGenericTypeDefinition();
                return def == typeof(Task<>) || def == typeof(ValueTask<>);
            }

            return false;
        }

        public static List<Type> GetRemoteInterfaces(Type type, bool checkIsGrainInterface = true)
        {
            var res = new List<Type>();

            if (IsGrainInterface(type))
                res.Add(type);

            foreach (var interfaceType in type.GetInterfaces())
                if (!checkIsGrainInterface || IsGrainInterface(interfaceType))
                    res.Add(interfaceType);

            return res;
        }

        public static int ComputeMethodId(MethodInfo methodInfo)
        {
            var attr = methodInfo.GetCustomAttribute<MethodIdAttribute>(true);
            if (attr != null) return attr.MethodId;

            var result = FormatMethodForIdComputation(methodInfo);
            return Utils.CalculateIdHash(result);
        }

        internal static string FormatMethodForIdComputation(MethodInfo methodInfo)
        {
            var strMethodId = new StringBuilder(methodInfo.Name);

            if (methodInfo.IsGenericMethodDefinition)
            {
                strMethodId.Append('<');
                var first = true;
                foreach (var arg in methodInfo.GetGenericArguments())
                {
                    if (!first) strMethodId.Append(',');
                    else first = false;
                    strMethodId.Append(RuntimeTypeNameFormatter.Format(arg));
                }

                strMethodId.Append('>');
            }

            strMethodId.Append('(');
            ParameterInfo[] parameters = methodInfo.GetParameters();
            bool bFirstTime = true;
            foreach (ParameterInfo info in parameters)
            {
                if (!bFirstTime)
                    strMethodId.Append(',');
                var pt = info.ParameterType;
                if (pt.IsGenericParameter)
                {
                    strMethodId.Append(pt.Name);
                }
                else
                {
                    strMethodId.Append(RuntimeTypeNameFormatter.Format(info.ParameterType));
                }

                bFirstTime = false;
            }

            strMethodId.Append(')');
            var result = strMethodId.ToString();
            return result;
        }

        public static int GetGrainInterfaceId(Type grainInterface)
        {
            return GetTypeCode(grainInterface);
        }

        public static int GetGrainClassTypeCode(Type grainClass)
        {
            return GetTypeCode(grainClass);
        }

        internal static void ValidateInterface(Type type)
        {
            if (!IsGrainInterface(type))
                throw new ArgumentException($"{type.FullName} is not a grain interface");

            var violations = new List<string>();
            if (!ValidateInterfaceMethods(type, violations) || !ValidateInterfaceProperties(type, violations))
            {
                if (ConsoleText.IsConsoleAvailable)
                {
                    foreach (var violation in violations)
                        ConsoleText.WriteLine("ERROR: " + violation);
                }

                throw new RulesViolationException(
                    string.Format("{0} does not conform to the grain interface rules.", type.FullName), violations);
            }
        }

        private static bool ValidateInterfaceMethods(Type type, List<string> violations)
        {
            bool success = true;

            MethodInfo[] methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Public);
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

                    if (parameter.ParameterType.IsByRef)
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

            PropertyInfo[] properties = type.GetProperties(BindingFlags.Instance | BindingFlags.Public);
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

        private sealed class MethodInfoComparer : IEqualityComparer<MethodInfo>
        {
            public bool Equals(MethodInfo x, MethodInfo y)
            {
                return x == y || ComputeMethodId(x) == ComputeMethodId(y);
            }

            public int GetHashCode(MethodInfo obj)
            {
                throw new NotImplementedException();
            }
        }

        /// <summary>
        /// Recurses through interface graph accumulating methods
        /// </summary>
        /// <param name="grainType">Grain type</param>
        /// <param name="serviceType">Service interface type</param>
        /// <param name="methodInfos">Accumulated </param>
        private static void GetMethodsImpl(Type grainType, Type serviceType, List<MethodInfo> methodInfos)
        {
            foreach (var iType in GetRemoteInterfaces(serviceType, false))
            {
                if (grainType.IsClass)
                {
                    var mapping = grainType.GetInterfaceMap(iType);
                    foreach (var methodInfo in iType.GetMethods(BindingFlags.Instance | BindingFlags.Public))
                    {
                        foreach (var info in mapping.TargetMethods)
                        {
                            if (info.DeclaringType == grainType && MethodComparer.Equals(methodInfo, info))
                            {
                                if (!methodInfos.Contains(methodInfo, MethodComparer))
                                    methodInfos.Add(methodInfo);
                                break;
                            }
                        }
                    }
                }
                else if (grainType.IsInterface)
                {
                    foreach (var methodInfo in iType.GetMethods(BindingFlags.Instance | BindingFlags.Public))
                    {
                        if (!methodInfos.Contains(methodInfo, MethodComparer))
                            methodInfos.Add(methodInfo);
                    }
                }
            }
        }

        private static int GetTypeCode(Type grainInterfaceOrClass)
        {
            var attrs = grainInterfaceOrClass.GetCustomAttributes(typeof(TypeCodeOverrideAttribute), false);
            if (attrs.Length > 0) return ((TypeCodeOverrideAttribute)attrs[0]).TypeCode;

            var fullName = GetFullName(grainInterfaceOrClass);
            return Utils.CalculateIdHash(fullName);
        }

        public static string GetFullName(Type grainInterfaceOrClass)
        {
            return TypeUtils.GetTemplatedName(
                TypeUtils.GetFullName(grainInterfaceOrClass),
                grainInterfaceOrClass,
                grainInterfaceOrClass.GetGenericArgumentsSafe(),
                t => false);
        }
    }
}
