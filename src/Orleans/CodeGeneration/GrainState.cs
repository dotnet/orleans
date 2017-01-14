using System;
using System.Collections.Generic;
using System.Reflection;

namespace Orleans
{
    /// <summary>
    /// Base class for generated grain state classes.
    /// </summary>
    [Serializable]
    [Obsolete]
    public abstract class GrainState
    {
        /// <summary>Initializes a new instance of <see cref="GrainState"/>.</summary>
        /// <param name="grainTypeFullName">The type of the associated grains that use this GrainState object. Used to initialize the <c>GrainType</c> property.</param>
        protected GrainState(string grainTypeFullName)
        {
            // TODO: remove after removing support for state interfaces
        }

        /// <summary>Initializes a new instance of <see cref="GrainState"/>.</summary>
        protected GrainState() { }

        /// <summary>
        /// Opaque value set by the storage provider representing an 'Etag' setting for the last time the state data was read from backing store.
        /// </summary>
        public string Etag { get; set; }

        /// <summary>
        /// Converts this property bag into a dictionary.
        /// This is a default Reflection-based implementation that can be overridden in the subclass or generated code.
        /// </summary>
        /// <returns>A Dictionary from string property name to property value.</returns>
        public virtual IDictionary<string, object> AsDictionary()
        {
            var result = new Dictionary<string, object>();

            var properties = this.GetType().GetTypeInfo().GetProperties();
            foreach (var property in properties)
                if (property.Name != "Etag")
                    result[property.Name] = property.GetValue(this);

            return result;
        }

        /// <summary>
        /// Populates this property bag from a dictionary.
        /// This is a default Reflection-based implementation that can be overridden in the subclass or generated code.
        /// </summary>
        /// <param name="values">The Dictionary from string to object that contains the values
        /// for this property bag.</param>
        public virtual void SetAll(IDictionary<string, object> values)
        {
            if (values == null)
            {
                ResetProperties();
                return;
            }

            var type = this.GetType();
            foreach (var key in values.Keys)
            {
                var property = type.GetProperty(key);
                // property doesn't have setter
                if (property.GetSetMethod() == null) { continue; }
                property.SetValue(this, values[key]);
            }
        }

        /// <summary>
        /// Resets properties of the state object to their default values.
        /// </summary>
        private void ResetProperties()
        {
            var properties = this.GetType().GetProperties();
            foreach (var property in properties)
            {
                // property doesn't have setter
                if (property.GetSetMethod() == null) { continue; }
                property.SetValue(this, null);
            }
        }
    }
}