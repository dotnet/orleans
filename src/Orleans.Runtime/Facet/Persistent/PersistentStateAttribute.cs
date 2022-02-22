using System;

namespace Orleans.Runtime
{
    /// <summary>
    /// Specifies options for the <see cref="IPersistentState{TState}"/> constructor argument which it is applied to.
    /// </summary>
    /// <seealso cref="System.Attribute" />
    /// <seealso cref="Orleans.IFacetMetadata" />
    /// <seealso cref="Orleans.Runtime.IPersistentStateConfiguration" />
    [AttributeUsage(AttributeTargets.Parameter)]
    public class PersistentStateAttribute : Attribute, IFacetMetadata, IPersistentStateConfiguration
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="PersistentStateAttribute"/> class.
        /// </summary>
        /// <param name="stateName">Name of the state.</param>
        /// <param name="storageName">Name of the storage provider.</param>
        public PersistentStateAttribute(string stateName, string storageName = null)
        {
            this.StateName = stateName;
            this.StorageName = storageName;
        }

        /// <summary>
        /// Gets the name of the state.
        /// </summary>
        /// <value>The name of the state.</value>
        public string StateName { get; }

        /// <summary>
        /// Gets the name of the storage provider.
        /// </summary>
        /// <value>The name of the storage provider.</value>
        public string StorageName { get; }
    }
}
