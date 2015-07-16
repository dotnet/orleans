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
using System.Reflection;


namespace Orleans.Runtime
{
    /// <summary>
    /// Provides strongly typed information regarding an assembly.
    /// </summary>
    public class AssemblyInfoCollection
    {
        /// <summary>
        /// A cached instance of the assembly (type information) from which to retrieve information.
        /// </summary>
        private readonly Assembly assembly;

        /// <summary>
        /// A default constructor.
        /// </summary>
        /// <param name="type">The type of the assembly.</param>
        public AssemblyInfoCollection(Type type)
        {
            //N.B. in CoreCLR this would likely use Assembly.Load
            //or some other method derived from examples at https://github.com/dotnet/coreclr/issues/919.
            assembly = Assembly.GetAssembly(type);
        }

        /// <summary>
        /// The title of the assembly.
        /// </summary>
        public string Title
        {
            get { return CustomAttributes<AssemblyTitleAttribute>().Title; }
        }

        /// <summary>
        /// The description of the assembly.
        /// </summary>
        public string Description
        {
            get { return CustomAttributes<AssemblyDescriptionAttribute>().Description; }
        }

        /// <summary>
        /// The company information of the assembly.
        /// </summary>
        public string Company
        {
            get { return CustomAttributes<AssemblyCompanyAttribute>().Company; }
        }

        /// <summary>
        /// The product information of the assembly.
        /// </summary>
        public string Product
        {
            get { return CustomAttributes<AssemblyProductAttribute>().Product; }
        }

        /// <summary>
        /// The copyright of the assembly.
        /// </summary>
        public string Copyright
        {
            get { return CustomAttributes<AssemblyCopyrightAttribute>().Copyright; }
        }

        /// <summary>
        /// The trademark information of the assembly.
        /// </summary>
        public string Trademark
        {
            get { return CustomAttributes<AssemblyTrademarkAttribute>().Trademark; }
        }

        /// <summary>
        /// The assembly version.
        /// </summary>
        public Version AssemblyVersion
        {
            get { return assembly.GetName().Version; }
        }

        /// <summary>
        /// Gets custom attributes from the assembly as specified by type.
        /// </summary>
        /// <typeparam name="T">The type of the attribute.</typeparam>
        /// <returns>The custom attribute from the assembly as specified by the type.</returns>
        private T CustomAttributes<T>() where T : Attribute
        {
            //N.B. In CoreCLR this likely uses assembly.CustomAttributes.
            var attributes = assembly.GetCustomAttributes(typeof(T), false);
            if(attributes != null || attributes.Length > 0)
            {
                return (T)attributes[0];
            }

            throw new InvalidOperationException();
        }
    }
}
