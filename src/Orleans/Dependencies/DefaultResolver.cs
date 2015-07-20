// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved. See License.txt in the project root for license information.

using System;
//using System.Collections.Generic;
//using System.Linq;

namespace Orleans.Runtime
{
    internal class DefaultResolver : IDependencyResolver
    {
        private static readonly IDependencyResolver _instance = new DefaultResolver();

        private DefaultResolver()
        {
        }

        public static IDependencyResolver Instance
        {
            get { return _instance; }
        }

        public IDependencyScope BeginScope()
        {
            return this;
        }

        public void Dispose()
        {
        }

        public object GetService(Type serviceType)
        {
            return Activator.CreateInstance(serviceType);
        }

        //public IEnumerable<object> GetServices(Type serviceType)
        //{
        //    return Enumerable.Empty<object>();
        //}
    }
}
