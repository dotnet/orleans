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
using System.CodeDom;
using System.Reflection;
using Orleans.CodeGeneration;

namespace Orleans.Runtime
{
    /// <summary>
    /// A collection of utility functions for dealing with Type information.
    /// </summary>
    internal static class TypeUtils
    {

        private static string GetSimpleNameHandleArray(TypeInfo typeInfo, Language language)
        {
            if (typeInfo.IsArray && language == Language.VisualBasic)
                return typeInfo.Name.Replace('[', '(').Replace(']', ')');

            return typeInfo.Name;
        }
        
        public static string GetSimpleTypeName(TypeInfo typeInfo, Func<TypeInfo, bool> fullName=null, Language language = Language.CSharp)
        {
            if (typeInfo.IsNestedPublic || typeInfo.IsNestedPrivate)
            {
                if (typeInfo.DeclaringType.IsGenericType)
                    return GetTemplatedName(GetUntemplatedTypeName(typeInfo.DeclaringType.Name), typeInfo.DeclaringType.GetTypeInfo(), typeInfo.GetGenericArguments(), _ => true, language) + "." + GetUntemplatedTypeName(typeInfo.Name);
                
                return GetTemplatedName(typeInfo.DeclaringType.GetTypeInfo(), language: language) + "." + GetUntemplatedTypeName(typeInfo.Name);
            }

            if (typeInfo.IsGenericType) return GetSimpleTypeName(fullName != null && fullName(typeInfo) ? GetFullName(typeInfo, language) : GetSimpleNameHandleArray(typeInfo, language));
            
            return fullName != null && fullName(typeInfo) ? GetFullName(typeInfo, language) : GetSimpleNameHandleArray(typeInfo, language: language);
        }

        public static string GetUntemplatedTypeName(string typeName)
        {
            int i = typeName.IndexOf('`');
            if (i > 0)
            {
                typeName = typeName.Substring(0, i);
            }
            i = typeName.IndexOf('<');
            if (i > 0)
            {
                typeName = typeName.Substring(0, i);
            }
            return typeName;            
        }

        public static string GetSimpleTypeName(string typeName)
        {
            int i = typeName.IndexOf('`');
            if (i > 0)
            {
                typeName = typeName.Substring(0, i);
            }
            i = typeName.IndexOf('[');
            if (i > 0)
            {
                typeName = typeName.Substring(0, i);
            }
            i = typeName.IndexOf('<');
            if (i > 0)
            {
                typeName = typeName.Substring(0, i);
            }
            return typeName;
        }

        public static bool IsConcreteTemplateType(TypeInfo typeInfo)
        {
            if (typeInfo.IsGenericType) return true;
            return typeInfo.IsArray && IsConcreteTemplateType(typeInfo.GetElementType().GetTypeInfo());
        }

        public static string GetTemplatedName(TypeInfo typeInfo, Func<TypeInfo, bool> fullName=null, Language language = Language.CSharp)
        {
            if (typeInfo.IsGenericType) return GetTemplatedName(GetSimpleTypeName(typeInfo, fullName, language), typeInfo, typeInfo.GetGenericArguments(), fullName, language);

            if (typeInfo.IsArray)
            {
                bool isVB = language == Language.VisualBasic;

                return GetTemplatedName(typeInfo.GetElementType().GetTypeInfo(), fullName)
                       + (isVB ? "(" : "[")
                       + new string(',', typeInfo.GetArrayRank() - 1)
                       + (isVB ? ")" : "]");
            }
            
            return GetSimpleTypeName(typeInfo, fullName, language);
        }

        public static string GetTemplatedName(string baseName, TypeInfo typeInfo, Type[] genericArguments, Func<TypeInfo, bool> fullName, Language language = Language.CSharp)
        {
            if (!typeInfo.IsGenericType || (typeInfo.DeclaringType != null && typeInfo.DeclaringType.IsGenericType)) return baseName;
            bool isVB = language == Language.VisualBasic;
            string s = baseName;
            s += isVB ? "(Of " : "<";
            s += GetGenericTypeArgs(genericArguments, fullName, language);
            s += isVB ? ")" : ">";
            return s;
        }

        public static string GetGenericTypeArgs(Type[] args, Func<TypeInfo, bool> fullName, Language language = Language.CSharp)
        {
            string s = String.Empty;

            bool first = true;
            foreach (var genericParameter in args)
            {
                if (!first)
                {
                    s += ",";
                }
                if (!genericParameter.IsGenericType)
                {
                    s += GetSimpleTypeName(genericParameter.GetTypeInfo(), fullName, language);
                }
                else
                {
                    s += GetTemplatedName(genericParameter.GetTypeInfo(), fullName, language);
                }
                first = false;
            }

            return s;
        }

        public static string GetParameterizedTemplateName(TypeInfo typeInfo, bool applyRecursively = false, Func<TypeInfo, bool> fullName = null, Language language = Language.CSharp)
        {
            if (fullName == null)
                fullName = tt => false;

            return GetParameterizedTemplateName(typeInfo, fullName, applyRecursively, language);
        }

        public static string GetParameterizedTemplateName(TypeInfo typeInfo, Func<TypeInfo, bool> fullName, bool applyRecursively = false, Language language = Language.CSharp)
        {
            if (typeInfo.IsGenericType)
            {
                return GetParameterizedTemplateName(GetSimpleTypeName(typeInfo, fullName), typeInfo, applyRecursively, fullName, language);
            }
            else
            {
                if(fullName != null && fullName(typeInfo)==true)
                {
                    return typeInfo.FullName;
                }
            }
            return typeInfo.Name;
        }

        public static string GetParameterizedTemplateName(string baseName, TypeInfo typeInfo, bool applyRecursively = false, Func<TypeInfo, bool> fullName = null, Language language = Language.CSharp)
        {
            if (fullName == null)
                fullName = tt => false;

            if (!typeInfo.IsGenericType) return baseName;

            bool isVB = language == Language.VisualBasic;
            string s = baseName;
            s += isVB ? "(Of " : "<";
            bool first = true;
            foreach (var genericParameter in typeInfo.GetGenericArguments())
            {
                var genericParameterTypeInfo = genericParameter.GetTypeInfo();
                if (!first)
                {
                    s += ",";
                }
                if (applyRecursively && genericParameterTypeInfo.IsGenericType)
                {
                    s += GetParameterizedTemplateName(genericParameterTypeInfo, applyRecursively, language: language);
                }
                else
                {
                    s += genericParameterTypeInfo.FullName == null || !fullName(genericParameterTypeInfo)
                        ? genericParameterTypeInfo.Name
                        : genericParameterTypeInfo.FullName;
                }
                first = false;
            }
            s += isVB ? ")" : ">";
            return s;
        }

        public static string GetRawClassName(string baseName, TypeInfo typeInfo)
        {
            return typeInfo.IsGenericType ? baseName + '`' + typeInfo.GetGenericArguments().Length : baseName;
        }

        public static string GetRawClassName(string typeName)
        {
            int i = typeName.IndexOf('[');
            return i <= 0 ? typeName : typeName.Substring(0, i);
        }

        public static Type[] GenericTypeArgs(string className)
        {
            var typeArgs = new List<Type>();
            var genericTypeDef = GenericTypeArgsString(className).Replace("[]", "##"); // protect array arguments
            string[] genericArgs = genericTypeDef.Split('[', ']');
            foreach (string genericArg in genericArgs)
            {
                string typeArg = genericArg.Trim('[', ']');
                if (typeArg.Length > 0 && typeArg != ",")
                    typeArgs.Add(Type.GetType(typeArg.Replace("##", "[]"))); // restore array arguments
            }
            return typeArgs.ToArray();
        }

        public static string GenericTypeArgsString(string className)
        {
            int startIndex = className.IndexOf('[');
            int endIndex = className.LastIndexOf(']');
            return className.Substring(startIndex + 1, endIndex - startIndex - 1);
        }

        public static CodeTypeParameterCollection GenericTypeParameters(TypeInfo typeInfo)
        {
            if (!typeInfo.IsGenericType) return null; 

            var p = new CodeTypeParameterCollection();
            foreach (var genericParameter in typeInfo.GetGenericTypeDefinition().GetGenericArguments())
            {
                var genericParameterTypeInfo = genericParameter.GetTypeInfo();
                var param = new CodeTypeParameter(genericParameterTypeInfo.Name);
                if ((genericParameterTypeInfo.GenericParameterAttributes &
                     GenericParameterAttributes.ReferenceTypeConstraint) != GenericParameterAttributes.None)
                {
                    param.Constraints.Add(" class");
                }
                if ((genericParameterTypeInfo.GenericParameterAttributes &
                     GenericParameterAttributes.NotNullableValueTypeConstraint) != GenericParameterAttributes.None)
                {
                    param.Constraints.Add(" struct");
                }
                var constraints = genericParameterTypeInfo.GetGenericParameterConstraints();
                foreach (var constraintType in constraints)
                {
                    param.Constraints.Add(
                        new CodeTypeReference(GetParameterizedTemplateName(constraintType.GetTypeInfo(), false,
                            x => true)));
                }
                if ((genericParameterTypeInfo.GenericParameterAttributes &
                     GenericParameterAttributes.DefaultConstructorConstraint) != GenericParameterAttributes.None)
                {
                    param.HasConstructorConstraint = true;
                }
                p.Add(param);
            }
            return p;
        }

        public static bool IsGenericClass(string name)
        {
            return name.Contains("`") || name.Contains("[");
        }

        public static string GetFullName(TypeInfo typeInfo, Language language = Language.CSharp)
        {
            if (typeInfo == null) throw new ArgumentNullException("t");
            if (typeInfo.IsNested && !typeInfo.IsGenericParameter)
            {
                return typeInfo.Namespace + "." + typeInfo.DeclaringType.Name + "." + GetSimpleNameHandleArray(typeInfo, language);
            }
            if (typeInfo.IsArray)
            {
                bool isVB = language == Language.VisualBasic;
                return GetFullName(typeInfo.GetElementType().GetTypeInfo(), language)
                       + (isVB ? "(" : "[")
                       + new string(',', typeInfo.GetArrayRank() - 1)
                       + (isVB ? ")" : "]");
            }
            return typeInfo.FullName ?? ( typeInfo.IsGenericParameter ? GetSimpleNameHandleArray(typeInfo, language) : typeInfo.Namespace + "." + GetSimpleNameHandleArray(typeInfo, language));
        }

        /// <summary>
        /// decide whether the class is derived from Grain
        /// </summary>
        public static bool IsGrainClass(TypeInfo typeInfo)
        {
            var grainTypeInfo = typeof(Grain).GetTypeInfo();
            var grainChevronTypeInfo = typeof(Grain<>).GetTypeInfo();
            if (typeInfo.Assembly.ReflectionOnly)
            {
                grainTypeInfo = ToReflectionOnlyType(grainTypeInfo);
                grainChevronTypeInfo = ToReflectionOnlyType(grainChevronTypeInfo);
            }

            if (grainTypeInfo == typeInfo || grainChevronTypeInfo == typeInfo) return false; 

            if (!grainTypeInfo.IsAssignableFrom(typeInfo)) return false;

            // exclude generated classes.
            return !IsGeneratedType(typeInfo);
        }

        public static bool IsSystemTargetClass(TypeInfo typeInfo)
        {
            TypeInfo systemTargetTypeInfo;
            if (!TryResolveType("Orleans.Runtime.SystemTarget", out systemTargetTypeInfo)) return false;

            var systemTargetInterfaceTypeInfo = typeof(ISystemTarget).GetTypeInfo();
            var systemTargetBaseInterfaceTypeInfo = typeof(ISystemTargetBase).GetTypeInfo();
            if (typeInfo.Assembly.ReflectionOnly)
            {
                systemTargetTypeInfo = ToReflectionOnlyType(systemTargetTypeInfo);
                systemTargetInterfaceTypeInfo = ToReflectionOnlyType(systemTargetInterfaceTypeInfo);
                systemTargetBaseInterfaceTypeInfo = ToReflectionOnlyType(systemTargetBaseInterfaceTypeInfo);
            }

            if (!systemTargetInterfaceTypeInfo.IsAssignableFrom(typeInfo) ||
                !systemTargetBaseInterfaceTypeInfo.IsAssignableFrom(typeInfo) ||
                !systemTargetTypeInfo.IsAssignableFrom(typeInfo)) return false;

            // exclude generated classes.
            return !IsGeneratedType(typeInfo);
        }

        public static bool IsConcreteGrainClass(TypeInfo typeInfo, out IEnumerable<string> complaints, bool complain)
        {
            complaints = null;
            if (!IsGrainClass(typeInfo)) return false;
            if (!typeInfo.IsAbstract) return true;

            complaints = complain ? new[] { string.Format("Grain type {0} is abstract and cannot be instantiated.", typeInfo.FullName) } : null;
            return false;
        }

        public static bool IsConcreteGrainClass(TypeInfo typeInfo, out IEnumerable<string> complaints)
        {
            return IsConcreteGrainClass(typeInfo, out complaints, complain: true);
        }

        public static bool IsConcreteGrainClass(TypeInfo typeInfo)
        {
            IEnumerable<string> complaints;
            return IsConcreteGrainClass(typeInfo, out complaints, complain: false);
        }

        public static bool IsGeneratedType(TypeInfo typeInfo)
        {
            return TypeHasAttribute(typeInfo, typeof(GeneratedAttribute).GetTypeInfo());
        }

        public static bool IsGrainMethodInvokerType(TypeInfo typeInfo)
        {
            var generalTypeInfo = typeof(IGrainMethodInvoker).GetTypeInfo();
            if (typeInfo.Assembly.ReflectionOnly)
            {
                generalTypeInfo = ToReflectionOnlyType(generalTypeInfo);
            }
            return generalTypeInfo.IsAssignableFrom(typeInfo) && TypeHasAttribute(typeInfo, typeof(MethodInvokerAttribute).GetTypeInfo());        
        }

        public static bool IsGrainStateType(TypeInfo typeInfo)
        {
            var generalTypeInfo = typeof(GrainState).GetTypeInfo();
            if (typeInfo.Assembly.ReflectionOnly)
            {
                generalTypeInfo = ToReflectionOnlyType(generalTypeInfo);
            }
            return generalTypeInfo.IsAssignableFrom(typeInfo) && TypeHasAttribute(typeInfo, typeof(GrainStateAttribute).GetTypeInfo());
        }
            
        public static Type ResolveType(string fullName)
        {
            return CachedTypeResolver.Instance.ResolveType(fullName);
        }

        public static bool TryResolveType(string fullName, out TypeInfo typeInfo)
        {
            return CachedTypeResolver.Instance.TryResolveType(fullName, out typeInfo);            
        }

        public static TypeInfo ResolveReflectionOnlyType(string assemblyQualifiedName)
        {
            return CachedReflectionOnlyTypeResolver.Instance.ResolveType(assemblyQualifiedName);
        }

        public static TypeInfo ToReflectionOnlyType(TypeInfo typeInfo)
        {
            return typeInfo.Assembly.ReflectionOnly ? typeInfo : ResolveReflectionOnlyType(typeInfo.AssemblyQualifiedName);
        }

        public static IEnumerable<Type> GetTypes(Assembly assembly, Func<Type, bool> whereFunc)
        {
            return assembly.IsDynamic ? Enumerable.Empty<Type>() : assembly.GetTypes().Where(type => !type.IsNestedPrivate && whereFunc(type));
        }

        public static IEnumerable<Type> GetTypes(Func<Type, bool> whereFunc)
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            var result = new List<Type>();
            foreach (var assembly in assemblies)
            {
                // there's no point in evaluating nested private types-- one of them fails to coerce to a reflection-only type anyhow.
                var types = GetTypes(assembly, whereFunc);
                result.AddRange(types);
            }
            return result;
        }

        public static IEnumerable<Type> GetTypes(List<string> assemblies, Func<Type, bool> whereFunc)
        {
            var currentAssemblies = AppDomain.CurrentDomain.GetAssemblies();
            var result = new List<Type>();
            foreach (var assembly in currentAssemblies.Where(loaded => !loaded.IsDynamic && assemblies.Contains(loaded.Location)))
            {
                // there's no point in evaluating nested private types-- one of them fails to coerce to a reflection-only type anyhow.
                var types = GetTypes(assembly, whereFunc);
                result.AddRange(types);
            }
            return result;
        }

        public static bool TypeHasAttribute(TypeInfo typeInfo, TypeInfo attribTypeInfo)
        {
            if (typeInfo.Assembly.ReflectionOnly || attribTypeInfo.Assembly.ReflectionOnly)
            {
                typeInfo = ToReflectionOnlyType(typeInfo);
                attribTypeInfo = ToReflectionOnlyType(attribTypeInfo);
            }

            // we can't use Type.GetCustomAttributes here because we could potentially be working with a reflection-only type.
            return CustomAttributeData.GetCustomAttributes(typeInfo).Any(
                    attrib => attribTypeInfo.IsAssignableFrom(attrib.AttributeType));
        }
    }
}
