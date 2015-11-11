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
using System.Linq;
using System.Reflection;
using Microsoft.Framework.DependencyInjection;

namespace Orleans.Runtime.Startup
{
    internal class ConfigureServicesBuilder
    {
        public ConfigureServicesBuilder(MethodInfo configureServices)
        {
            if (configureServices == null)
            {
                throw new ArgumentNullException("configureServices");
            }

            // Only support IServiceCollection parameters
            var parameters = configureServices.GetParameters();
            if (parameters.Length > 1 ||
                parameters.Any(p => p.ParameterType != typeof(IServiceCollection)))
            {
                throw new InvalidOperationException("ConfigureServices can take at most a single IServiceCollection parameter.");
            }

            MethodInfo = configureServices;
        }

        public MethodInfo MethodInfo { get; private set; }

        public IServiceProvider Build (object instance, IServiceCollection services)
        {
            if (instance == null)
            {
                throw new ArgumentNullException("instance");
            }
            if (services == null)
            {
                throw new ArgumentNullException("services");
            }
            return Invoke(instance, services);
        }

        private IServiceProvider Invoke(object instance, IServiceCollection exportServices)
        {
            var parameters = new object[MethodInfo.GetParameters().Length];

            // Ctor ensures we have at most one IServiceCollection parameter
            if (parameters.Length > 0)
            {
                parameters[0] = exportServices;
            }

            //
            // For Orleans we've a a modified behavior, different from Asp.Net vNext, since Orleans will not fallback to
            // default DI implementation if the ConfigureServices method is not returning a build DI container.
            //

            var serviceProvider = MethodInfo.Invoke(instance, parameters) as IServiceProvider;

            if (serviceProvider == null)
            {
                throw new InvalidOperationException("The ConfigureServices method did not returned a configured IServiceProvider instance.");
            }

            return serviceProvider;
        }
    }
}
