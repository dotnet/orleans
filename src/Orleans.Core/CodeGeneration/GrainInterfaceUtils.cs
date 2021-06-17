using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Orleans.Runtime;
using Orleans.Serialization.TypeSystem;

namespace Orleans.CodeGeneration
{
    internal static class GrainInterfaceUtils
    {
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
            {
                if (!methodInfos.Contains(methodInfo, MethodInfoComparer.Default))
                {
                    methodInfos.Add(methodInfo);
                }
            }

            return methodInfos.ToArray();

            static void GetMethodsImpl(Type grainType, Type serviceType, List<MethodInfo> methodInfos)
            {
                foreach (var iType in GetGrainInterfaces(serviceType))
                {
                    if (grainType.IsClass)
                    {
                        var mapping = grainType.GetInterfaceMap(iType);
                        foreach (var methodInfo in iType.GetMethods(BindingFlags.Instance | BindingFlags.Public))
                        {
                            foreach (var info in mapping.TargetMethods)
                            {
                                if (info.DeclaringType == grainType && MethodInfoComparer.Default.Equals(methodInfo, info))
                                {
                                    if (!methodInfos.Contains(methodInfo, MethodInfoComparer.Default))
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
                            if (!methodInfos.Contains(methodInfo, MethodInfoComparer.Default))
                                methodInfos.Add(methodInfo);
                        }
                    }
                }
            }

            static List<Type> GetGrainInterfaces(Type type)
            {
                var res = new List<Type>();

                if (IsGrainInterface(type))
                {
                    res.Add(type);
                }

                foreach (var interfaceType in type.GetInterfaces())
                {
                    res.Add(interfaceType);
                }

                return res;
            }
        }


        public static int GetGrainClassTypeCode(Type grainClass)
        {
            var fullName = RuntimeTypeNameFormatter.Format(grainClass);
            return Utils.CalculateIdHash(fullName);
        }

        private sealed class MethodInfoComparer : IEqualityComparer<MethodInfo>, IComparer<MethodInfo>
        {
            public static MethodInfoComparer Default { get; } = new();

            private MethodInfoComparer()
            {
            }

            public bool Equals(MethodInfo x, MethodInfo y)
            {
                if (!string.Equals(x.Name, y.Name, StringComparison.Ordinal))
                {
                    return false;
                }

                var xArgs = x.GetGenericArguments();
                var yArgs = y.GetGenericArguments();
                if (xArgs.Length != yArgs.Length)
                {
                    return false;
                }

                for (var i = 0; i < x.GetGenericArguments().Length; i++)
                {
                    if (xArgs[i] != yArgs[i])
                    {
                        return false;
                    }
                }

                var xParams = x.GetParameters();
                var yParams = y.GetParameters();
                if (xParams.Length != yParams.Length)
                {
                    return false;
                }

                for (var i = 0; i < xParams.Length; i++)
                {
                    if (xParams[i].ParameterType != yParams[i].ParameterType)
                    {
                        return false;
                    }
                }

                return true;
            }

            public int GetHashCode(MethodInfo obj)
            {
                int hashCode = -499943048;
                hashCode = hashCode * -1521134295 + StringComparer.Ordinal.GetHashCode(obj.Name);

                foreach (var arg in obj.GetGenericArguments())
                {
                    hashCode = hashCode * -1521134295 + arg.GetHashCode();
                }

                foreach (var parameter in obj.GetParameters())
                {
                    hashCode = hashCode * -1521134295 + parameter.ParameterType.GetHashCode();
                }

                return hashCode;
            }

            public int Compare(MethodInfo x, MethodInfo y)
            {
                var result = StringComparer.Ordinal.Compare(x.Name, y.Name);
                if (result != 0)
                {
                    return result;
                }

                var xArgs = x.GetGenericArguments();
                var yArgs = y.GetGenericArguments();
                result = xArgs.Length.CompareTo(yArgs.Length);
                if (result != 0)
                {
                    return result;
                }

                for (var i = 0; i < xArgs.Length; i++)
                {
                    var xh = xArgs[i].GetHashCode();
                    var yh = yArgs[i].GetHashCode();
                    result = xh.CompareTo(yh);
                    if (result != 0)
                    {
                        return result;
                    }
                }

                var xParams = x.GetParameters();
                var yParams = y.GetParameters();
                result = xParams.Length.CompareTo(yParams.Length);
                if (result != 0)
                {
                    return result;
                }

                for (var i = 0; i < xParams.Length; i++)
                {
                    var xh = xParams[i].ParameterType.GetHashCode();
                    var yh = yParams[i].ParameterType.GetHashCode();
                    result = xh.CompareTo(yh);
                    if (result != 0)
                    {
                        return result;
                    }
                }

                return 0;
            }
        }
    }
}
