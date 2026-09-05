using Orleans.Serialization.Configuration;
using Orleans.Serialization.Serializers;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Serialization
{
    /// <summary>
    /// Analyzes serializer configuration to find likely configuration issues.
    /// </summary>
    public static class SerializerConfigurationAnalyzer
    {
        /// <summary>
        /// Analyzes grain interface methods to find parameter types and return types which are not serializable.
        /// </summary>
        /// <param name="codecProvider">
        /// The codec provider.
        /// </param>
        /// <param name="options">
        /// The type manifest options.
        /// </param>
        /// <returns>
        /// A collection of types which have serializability issues.
        /// </returns>
        public static Dictionary<Type, SerializerConfigurationComplaint> AnalyzeSerializerAvailability(ICodecProvider codecProvider, TypeManifestOptions options)
        {
            var allComplaints = new Dictionary<Type, SerializerConfigurationComplaint>();
            foreach (var @interface in options.Interfaces)
            {
                foreach (var method in GetInterfaceMethods(@interface))
                {
                    if (typeof(Task).IsAssignableFrom(method.ReturnType))
                    {
                        if (method.ReturnType.IsConstructedGenericType && typeof(Task<>).IsAssignableFrom(method.ReturnType.GetGenericTypeDefinition()))
                        {
                            VisitType(method.ReturnType.GetGenericArguments()[0], method);
                        }
                    }

                    if (method.ReturnType.IsConstructedGenericType && typeof(ValueTask<>).IsAssignableFrom(method.ReturnType.GetGenericTypeDefinition()))
                    {
                        VisitType(method.ReturnType.GetGenericArguments()[0], method);
                    }

                    foreach (var param in method.GetParameters())
                    {
                        VisitType(param.ParameterType, method);
                    }
                }
            }

            return allComplaints;

            void VisitType(Type type, MethodInfo methodInfo)
            {
                if (!IsEligibleType(type))
                {
                    return;
                }

                var hasCodec = codecProvider.TryGetCodec(type) is not null;
                var hasCopier = codecProvider.TryGetDeepCopier(type) is not null;
                if (!hasCodec || !hasCopier)
                {
                    if (!allComplaints.TryGetValue(type, out var complaint))
                    {
                        complaint = allComplaints[type] = new()
                        {
                            HasSerializer = hasCodec,
                            HasCopier = hasCopier,
                        };
                    }

                    if (!complaint.Methods.TryGetValue(methodInfo.DeclaringType!, out var methodList))
                    {
                        methodList = complaint.Methods[methodInfo.DeclaringType!] = new HashSet<MethodInfo>();
                    }

                    methodList.Add(methodInfo);
                }
            }

            bool IsEligibleType(Type type)
            {
                if (type.IsGenericTypeParameter || type.IsGenericMethodParameter || type.ContainsGenericParameters || type.Equals(typeof(CancellationToken)))
                {
                    return false;
                }

                return true;
            }
        }

#if NET5_0_OR_GREATER
        [UnconditionalSuppressMessage(
            "Trimming",
            "IL2070",
            Justification = "Generated manifests and trim-safe manual configuration register grain interfaces through TypeManifestOptions.AddInterface, which preserves public and non-public methods and inherited interfaces. The HashSet<Type> boundary cannot retain that annotation.")]
#endif
        private static MethodInfo[] GetInterfaceMethods(Type interfaceType)
            => interfaceType.GetMethods(BindingFlags.Instance | BindingFlags.Public);

        /// <summary>
        /// Represents a configuration issue regarding the serializability of a type used in interface methods.
        /// </summary>
        public class SerializerConfigurationComplaint
        {
            /// <summary>
            /// Gets a collection of interface types which reference the type this complaint represents.
            /// </summary>
            public Dictionary<Type, HashSet<MethodInfo>> Methods { get; } = new();

            /// <summary>
            /// Gets or sets a value indicating whether a serializer is available for this type.
            /// </summary>
            public bool HasSerializer { get; set; }

            /// <summary>
            /// Gets or sets a value indicating whether a copier is available for this type.
            /// </summary>
            public bool HasCopier { get; set; }
        }
    }
}
