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

        private sealed class MethodInfoComparer : IEqualityComparer<MethodInfo>
        {
            public bool Equals(MethodInfo x, MethodInfo y)
            {
                return x == y || ComputeMethodId(x) == ComputeMethodId(y);
            }

            public int GetHashCode(MethodInfo obj)
            {
                return ComputeMethodId(obj);
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
