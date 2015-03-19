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

﻿using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;


namespace Orleans.Providers
{
    #pragma warning disable 1574
    /// <summary>
    /// Base interface for all type-specific provider interfaces in Orleans
    /// </summary>
    /// <seealso cref="Orleans.Providers.IBootstrapProvider"/>
    /// <seealso cref="Orleans.Storage.IStorageProvider"/>
    public interface IProvider
    {
        /// <summary>The name of this provider instance, as given to it in the config.</summary>
        string Name { get; }

        /// <summary>
        /// Initialization function called by Orleans Provider Manager when a new provider class instance  is created
        /// </summary>
        /// <param name="name">Name assigned for this provider</param>
        /// <param name="providerRuntime">Callback for accessing system functions in the Provider Runtime</param>
        /// <param name="config">Configuration metadata to be used for this provider instance</param>
        /// <returns>Completion promise Task for the inttialization work for this provider</returns>
        Task Init(string name, IProviderRuntime providerRuntime, IProviderConfiguration config);

        // For now, I've decided to keep Close in the per-provider interface and not as part of the common IProvider interface.
        // There is currently no central place where Close can / would be called. 
        // It might eventually be provided by xProviderManager classes in certain cases, 
        //  for example: if they detect silo shutdown in progress.

        //Task Close();
    }
    #pragma warning restore 1574

    /// <summary>
    /// Internal provider management interface for instantiating dependent providers in a hierarchical tree of dependencies
    /// </summary>
    internal interface IProviderManager
    {
        /// <summary>
        /// Call into Provider Manager for instantiating dependent providers in a hierarchical tree of dependencies
        /// </summary>
        /// <param name="name">Name of the provider to be found</param>
        /// <returns>Provider instance with the given name</returns>
        IProvider GetProvider(string name);
    }

    /// <summary>
    /// Configuration information that a provider receives
    /// </summary>
    public interface IProviderConfiguration
    {
        /// <summary>
        /// Full type name of this provider.
        /// </summary>
        string Type { get; }

        /// <summary>
        /// Name of this provider.
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Configuration properties for this provider instance, as name-value pairs.
        /// </summary>
        ReadOnlyDictionary<string, string> Properties { get; }

        /// <summary>
        /// Nested providers in case of a hierarchical tree of dependencies
        /// </summary>
        IList<IProvider> Children { get; }

        /// <summary>
        /// Set a property in this provider configuration.
        /// If the property with this key already exists, it is been overwritten with the new value, otherwise it is just added.
        /// </summary>
        /// <param name="key">The key of the property</param>
        /// <param name="val">The value of the property</param>
        /// <returns>Provider instance with the given name</returns>
        void SetProperty(string key, string val);

        /// <summary>
        /// Removes a property in this provider configuration.
        /// </summary>
        /// <param name="key">The key of the property.</param>
        /// <returns>True if the property was found and removed, false otherwise.</returns>
        bool RemoveProperty(string key);

    }
}