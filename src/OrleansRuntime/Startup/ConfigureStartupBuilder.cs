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
using Orleans.Runtime.Management;
using Orleans.Runtime.MembershipService;
using Orleans.Runtime.ReminderService;
using Orleans.Storage;
using Orleans.Streams;

namespace Orleans.Runtime.Startup
{
    internal class ConfigureStartupBuilder
    {
        internal static IServiceProvider ConfigureStartup(string startupTypeName)
        {
            if (String.IsNullOrWhiteSpace(startupTypeName))
            {
                return new DefaultServiceProvider();
            }

            var startupType = Type.GetType(startupTypeName);

            if (startupType == null)
            {
                throw new InvalidOperationException(string.Format("Can not locate the type specified in the configuration file: '{0}'.", startupTypeName));
            }

            var servicesMethod = FindConfigureServicesDelegate(startupType);

            if ((servicesMethod != null && !servicesMethod.MethodInfo.IsStatic))
            {
                var instance = Activator.CreateInstance(startupType);

                IServiceCollection serviceCollection = RegisterSystemTypes();
                return servicesMethod.Build(instance, serviceCollection);
            }
            return new DefaultServiceProvider();
        }

        private static IServiceCollection RegisterSystemTypes()
        {
            //
            // Register the system classes and grains in this method.
            //

            IServiceCollection serviceCollection = new ServiceCollection();

            serviceCollection.AddTransient<ManagementGrain>();
            serviceCollection.AddTransient<GrainBasedMembershipTable>();
            serviceCollection.AddTransient<GrainBasedReminderTable>();
            serviceCollection.AddTransient<PubSubRendezvousGrain>();
            serviceCollection.AddTransient<MemoryStorageGrain>();

            return serviceCollection;
        }

        private static ConfigureServicesBuilder FindConfigureServicesDelegate(Type startupType)
        {
            var servicesMethod = FindMethod(startupType, "ConfigureServices", typeof(IServiceProvider), required: false);

            return servicesMethod == null ? null : new ConfigureServicesBuilder(servicesMethod);
        }

        private static MethodInfo FindMethod(Type startupType, string methodName, Type returnType = null, bool required = true)
        {
            var methods = startupType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);
            var selectedMethods = methods.Where(method => method.Name.Equals(methodName)).ToList();

            if (selectedMethods.Count > 1)
            {
                throw new InvalidOperationException(string.Format("Having multiple overloads of method '{0}' is not supported.", methodName));
            }

            var methodInfo = selectedMethods.FirstOrDefault();

            if (methodInfo == null)
            {
                if (required)
                {
                    throw new InvalidOperationException(string.Format("A method named '{0}' in the type '{1}' could not be found.",
                        methodName,
                        startupType.FullName));
                }

                return null;
            }

            if (returnType != null && methodInfo.ReturnType != returnType)
            {
                if (required)
                {
                    throw new InvalidOperationException(string.Format("The '{0}' method in the type '{1}' must have a return type of '{2}'.",
                        methodInfo.Name,
                        startupType.FullName,
                        returnType.Name));
                }

                return null;
            }

            return methodInfo;
        }

        private class DefaultServiceProvider : IServiceProvider
        {
            public object GetService(Type serviceType)
            {
                return Activator.CreateInstance(serviceType);
            }
        }
    }
}
