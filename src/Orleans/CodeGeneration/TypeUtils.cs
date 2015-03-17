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

﻿using System;
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

        private static string GetSimpleNameHandleArray(Type t, Language language)
        {
            if (t.IsArray && language == Language.VisualBasic)
                return t.Name.Replace('[', '(').Replace(']', ')');

            return t.Name;
        }
        
        public static string GetSimpleTypeName(Type t, Func<Type, bool> fullName=null, Language language = Language.CSharp)
        {
            if (t.IsNestedPublic || t.IsNestedPrivate)
            {
                if (t.DeclaringType.IsGenericType)
                    return GetTemplatedName(GetUntemplatedTypeName(t.DeclaringType.Name), t.DeclaringType, t.GetGenericArguments(), _ => true, language) + "." + GetUntemplatedTypeName(t.Name, language);
                
                return GetTemplatedName(t.DeclaringType, language: language) + "." + GetUntemplatedTypeName(t.Name, language: language);
            }

            if (t.IsGenericType) return GetSimpleTypeName(fullName != null && fullName(t) ? GetFullName(t, language) : GetSimpleNameHandleArray(t, language));
            
            return fullName != null && fullName(t) ? GetFullName(t, language) : GetSimpleNameHandleArray(t, language: language);
        }

        public static string GetUntemplatedTypeName(string typeName, Language language = Language.CSharp)
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

        public static bool IsConcreteTemplateType(Type t)
        {
            if (t.IsGenericType) return true;
            return t.IsArray && IsConcreteTemplateType(t.GetElementType());
        }

        public static string GetTemplatedName(Type t, Func<Type, bool> fullName=null, Language language = Language.CSharp)
        {
            if (t.IsGenericType) return GetTemplatedName(GetSimpleTypeName(t, fullName, language), t, t.GetGenericArguments(), fullName, language);

            if (t.IsArray)
            {
                bool isVB = language == Language.VisualBasic;

                return GetTemplatedName(t.GetElementType(), fullName)
                       + (isVB ? "(" : "[")
                       + new string(',', t.GetArrayRank() - 1)
                       + (isVB ? ")" : "]");
            }
            
            return GetSimpleTypeName(t, fullName, language);
        }

        public static string GetTemplatedName(string baseName, Type t, Type[] genericArguments, Func<Type, bool> fullName, Language language = Language.CSharp)
        {
            if (!t.IsGenericType || (t.DeclaringType != null && t.DeclaringType.IsGenericType)) return baseName;
            bool isVB = language == Language.VisualBasic;
            string s = baseName;
            s += isVB ? "(Of " : "<";
            s += GetGenericTypeArgs(genericArguments, fullName, language);
            s += isVB ? ")" : ">";
            return s;
        }

        public static string GetGenericTypeArgs(Type[] args, Func<Type, bool> fullName, Language language = Language.CSharp)
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
                    s += GetSimpleTypeName(genericParameter, fullName, language);
                }
                else
                {
                    s += GetTemplatedName(genericParameter, fullName, language);
                }
                first = false;
            }

            return s;
        }

        public static string GetParameterizedTemplateName(Type t, bool applyRecursively = false, Func<Type, bool> fullName = null, Language language = Language.CSharp)
        {
            if (fullName == null)
                fullName = tt => false;

            return GetParameterizedTemplateName(t, fullName, applyRecursively, language);
        }

        public static string GetParameterizedTemplateName(Type t, Func<Type, bool> fullName, bool applyRecursively = false, Language language = Language.CSharp)
        {
            return t.IsGenericType ? GetParameterizedTemplateName(GetSimpleTypeName(t, fullName), t, applyRecursively, fullName, language) : t.FullName;
        }

        public static string GetParameterizedTemplateName(string baseName, Type t, bool applyRecursively = false, Func<Type, bool> fullName = null, Language language = Language.CSharp)
        {
            if (fullName == null)
                fullName = tt => false;

            if (!t.IsGenericType) return baseName;

            bool isVB = language == Language.VisualBasic;
            string s = baseName;
            s += isVB ? "(Of " : "<";
            bool first = true;
            foreach (var genericParameter in t.GetGenericArguments())
            {
                if (!first)
                {
                    s += ",";
                }
                if (applyRecursively && genericParameter.IsGenericType)
                {
                    s += GetParameterizedTemplateName(genericParameter, applyRecursively, language: language);
                }
                else
                {
                    s += genericParameter.FullName == null || !fullName(genericParameter)
                        ? genericParameter.Name
                        : genericParameter.FullName;
                }
                first = false;
            }
            s += isVB ? ")" : ">";
            return s;
        }

        public static string GetRawClassName(string baseName, Type t)
        {
            return t.IsGenericType ? baseName + '`' + t.GetGenericArguments().Length : baseName;
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

        public static CodeTypeParameterCollection GenericTypeParameters(Type t)
        {
            if (!t.IsGenericType) return null; 

            var p = new CodeTypeParameterCollection();
            foreach (var genericParameter in t.GetGenericTypeDefinition().GetGenericArguments())
            {
                var param = new CodeTypeParameter(genericParameter.Name);
                if ((genericParameter.GenericParameterAttributes &
                     GenericParameterAttributes.ReferenceTypeConstraint) != GenericParameterAttributes.None)
                {
                    param.Constraints.Add(" class");
                }
                if ((genericParameter.GenericParameterAttributes &
                     GenericParameterAttributes.NotNullableValueTypeConstraint) != GenericParameterAttributes.None)
                {
                    param.Constraints.Add(" struct");
                }
                var constraints = genericParameter.GetGenericParameterConstraints();
                foreach (var constraintType in constraints)
                {
                    param.Constraints.Add(
                        new CodeTypeReference(TypeUtils.GetParameterizedTemplateName(constraintType, false,
                            x => true)));
                }
                if ((genericParameter.GenericParameterAttributes &
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

        public static string GetFullName(Type t, Language language = Language.CSharp)
        {
            if (t == null) throw new ArgumentNullException("t");
            if (t.IsNested && !t.IsGenericParameter)
            {
                return t.Namespace + "." + t.DeclaringType.Name + "." + GetSimpleNameHandleArray(t, language);
            }
            if (t.IsArray)
            {
                bool isVB = language == Language.VisualBasic;
                return GetFullName(t.GetElementType(), language)
                       + (isVB ? "(" : "[")
                       + new string(',', t.GetArrayRank() - 1)
                       + (isVB ? ")" : "]");
            }
            return t.FullName ?? ( t.IsGenericParameter ? GetSimpleNameHandleArray(t, language) : t.Namespace + "." + GetSimpleNameHandleArray(t, language));
        }

        /// <summary>
        /// decide whether the class is derived from Grain
        /// </summary>
        public static bool IsGrainClass(Type type)
        {
            var grainType = typeof(Grain);
            var grainChevronType = typeof(Grain<>);
            if (type.Assembly.ReflectionOnly)
            {
                grainType = ToReflectionOnlyType(grainType);
                grainChevronType = ToReflectionOnlyType(grainChevronType);
            }

            if (grainType == type || grainChevronType == type) return false; 

            if (!grainType.IsAssignableFrom(type)) return false;

            // exclude generated classes.
            return !IsGeneratedType(type);
        }

        public static bool IsSystemTargetClass(Type type)
        {
            Type systemTargetType;
            if (!TryResolveType("Orleans.Runtime.SystemTarget", out systemTargetType)) return false; 

            var systemTargetInterfaceType = typeof(ISystemTarget);
            var systemTargetBaseInterfaceType = typeof(ISystemTargetBase);
            if (type.Assembly.ReflectionOnly)
            {
                systemTargetType = ToReflectionOnlyType(systemTargetType);
                systemTargetInterfaceType = ToReflectionOnlyType(systemTargetInterfaceType);
                systemTargetBaseInterfaceType = ToReflectionOnlyType(systemTargetBaseInterfaceType);
            }

            if (!systemTargetInterfaceType.IsAssignableFrom(type) || 
                !systemTargetBaseInterfaceType.IsAssignableFrom(type) || 
                !systemTargetType.IsAssignableFrom(type)) return false;

            // exclude generated classes.
            return !IsGeneratedType(type);
        }

        public static bool IsConcreteGrainClass(Type type, out IEnumerable<string> complaints, bool complain)
        {
            complaints = null;
            if (!IsGrainClass(type)) return false;
            if (!type.IsAbstract) return true;

            complaints = complain ? new [] { string.Format("Grain type {0} is abstract and cannot be instantiated.", type.FullName) } : null;
            return false;
        }

        public static bool IsConcreteGrainClass(Type type, out IEnumerable<string> complaints)
        {
            return IsConcreteGrainClass(type, out complaints, complain: true);
        }

        public static bool IsConcreteGrainClass(Type type)
        {
            IEnumerable<string> complaints;
            return IsConcreteGrainClass(type, out complaints, complain: false);
        }

        public static bool IsGeneratedType(Type type)
        {
            return TypeHasAttribute(type, typeof(GeneratedAttribute));
        }

        public static bool IsGrainMethodInvokerType(Type type)
        {
            var generalType = typeof(IGrainMethodInvoker);
            if (type.Assembly.ReflectionOnly)
            {
                generalType = ToReflectionOnlyType(generalType);
            }
            return generalType.IsAssignableFrom(type) && TypeHasAttribute(type, typeof(MethodInvokerAttribute));        
        }

        public static bool IsGrainStateType(Type type)
        {
            var generalType = typeof(GrainState);
            if (type.Assembly.ReflectionOnly)
            {
                generalType = ToReflectionOnlyType(generalType);
            }
            return generalType.IsAssignableFrom(type) && TypeHasAttribute(type, typeof(GrainStateAttribute));
        }
            
        public static Type ResolveType(string fullName)
        {
            return CachedTypeResolver.Instance.ResolveType(fullName);
        }

        public static bool TryResolveType(string fullName, out Type type)
        {
            return CachedTypeResolver.Instance.TryResolveType(fullName, out type);            
        }

        public static Type ResolveReflectionOnlyType(string assemblyQualifiedName)
        {
            return CachedReflectionOnlyTypeResolver.Instance.ResolveType(assemblyQualifiedName);
        }

        public static Type ToReflectionOnlyType(Type type)
        {
            return type.Assembly.ReflectionOnly ? type : ResolveReflectionOnlyType(type.AssemblyQualifiedName);
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

        public static bool TypeHasAttribute(Type type, Type attribType)
        {
            if (type.Assembly.ReflectionOnly || attribType.Assembly.ReflectionOnly)
            {
                type = ToReflectionOnlyType(type);
                attribType = ToReflectionOnlyType(attribType);
            }

            // we can't use Type.GetCustomAttributes here because we could potentially be working with a reflection-only type.
            return CustomAttributeData.GetCustomAttributes(type).Any(
                    attrib => attribType.IsAssignableFrom(attrib.AttributeType));
        }
    }
}
