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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;

namespace Orleans.Runtime
{
    internal class CachedTypeResolver
    {
        private readonly ConcurrentDictionary<string, Type> cache;

        static CachedTypeResolver()
        {
            Instance = new CachedTypeResolver();
        }
        public static CachedTypeResolver Instance { get; private set; }

        protected CachedTypeResolver()
        {
            cache = new ConcurrentDictionary<string, Type>();
        }

        public Type ResolveType(string name)
        {
            Type result;
            if (TryResolveType(name, out result)) return result;
            
            throw new KeyNotFoundException(string.Format("Unable to find a type named {0}", name));
        }

        public bool TryResolveType(string name, out Type type)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A FullName must not be null nor consist of only whitespace.", "name");
            if (TryGetCachedType(name, out type)) return true;
            if (!TryPerformUncachedTypeResolution(name, out type)) return false;

            AddTypeToCache(name, type);
            return true;
        }

        protected virtual bool TryPerformUncachedTypeResolution(string name, out Type type)
        {
            IEnumerable<Assembly> assemblies = AppDomain.CurrentDomain.GetAssemblies();
            if (!TryPerformUncachedTypeResolution(name, out type, assemblies)) return false;
            if (type.Assembly.ReflectionOnly) throw new InvalidOperationException(string.Format("Type resolution for {0} yielded reflection-only type.", name));
            return true;
        }

        private bool TryGetCachedType(string name, out Type result)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("type name was null or whitespace");
            return cache.TryGetValue(name, out result);
        }

        private void AddTypeToCache(string name, Type type)
        {
            Type entry = cache.GetOrAdd(name, _ => type);
            if (!ReferenceEquals(entry, type)) throw new InvalidOperationException("inconsistent type name association");
        }

        private static bool TryPerformUncachedTypeResolution(string fullName, out Type type, IEnumerable<Assembly> assemblies)
        {
            if (null == assemblies) throw new ArgumentNullException("assemblies");
            if (string.IsNullOrWhiteSpace(fullName)) throw new ArgumentException("A type name must not be null nor consist of only whitespace.", "fullName");

            foreach (var assembly in assemblies)
            {
                type = assembly.GetType(fullName, false);
                if (type != null)
                {
                    return true;
                }
            }

            type = null;
            return false;
        }
    }
}
