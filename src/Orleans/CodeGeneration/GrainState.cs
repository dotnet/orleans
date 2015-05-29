﻿/*
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

 //#define REREAD_STATE_AFTER_WRITE_FAILED

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Threading.Tasks;
using Orleans.AzureUtils;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Storage;

namespace Orleans.CodeGeneration
{

    /// <summary>
    /// Base class for generated grain state classes.
    /// </summary>
    [Serializable]
    public abstract class GrainState : IGrainState
    {
        private readonly string grainTypeName;

        /// <summary>
        /// This is used for serializing the state, so all base class fields must be here
        /// </summary>
        internal IDictionary<string, object> AsDictionaryInternal()
        {
            var result = AsDictionary();
            return result;
        }

        /// <summary>
        /// This is used for serializing the state, so all base class fields must be here
        /// </summary>
        internal void SetAllInternal(IDictionary<string, object> values)
        {
            if (values == null) values = new Dictionary<string, object>();
            SetAll(values);
        }

        internal void InitState(Dictionary<string, object> values)
        {
            SetAllInternal(values); // Overwrite grain state with new values
        }

        /// <summary>
        /// Called from generated code.
        /// </summary>
        /// <returns>Deep copy of this grain state object.</returns>
        public GrainState DeepCopy()
        {
            // NOTE: Cannot use SerializationManager.DeepCopy[Inner] functionality here without StackOverflowException!
            var values = this.AsDictionaryInternal();
            var copiedData = SerializationManager.DeepCopyInner(values) as IDictionary<string, object>;
            var copy = (GrainState)this.MemberwiseClone();
            copy.SetAllInternal(copiedData);
            return copy;
        }

        private static readonly Type wireFormatType = typeof(Dictionary<string, object>);

        /// <summary>
        /// Called from generated code.
        /// </summary>
        /// <param name="stream">Stream to serialize this grain state object to.</param>
        public void SerializeTo(BinaryTokenStreamWriter stream)
        {
            var values = this.AsDictionaryInternal();
            SerializationManager.SerializeInner(values, stream, wireFormatType);
        }

        /// <summary>
        /// Called from generated code.
        /// </summary>
        /// <param name="stream">Stream to recover / repopulate this grain state object from.</param>
        public void DeserializeFrom(BinaryTokenStreamReader stream)
        {
            var values = (Dictionary<string, object>)SerializationManager.DeserializeInner(wireFormatType, stream);
            this.SetAllInternal(values);
        }

        /// <summary>
        /// Constructs a new grain state object for a grain.
        /// </summary>
        /// <param name="reference">The type of the associated grains that use this GrainState object. Used to initialize the <c>GrainType</c> property.</param>
        protected GrainState(string grainTypeFullName)
        {
            grainTypeName = grainTypeFullName;
        }

        #region IGrainState storage operation methods
        /// <summary>
        /// Async method to cause refresh of the current grain state data from backing store.
        /// Any previous contents of the grain state data will be overwritten.
        /// </summary>
        [Obsolete("Use Grain.Storage.ReadStateAsync method instead.")]
        public async Task ReadStateAsync()
        {
            var grain = RuntimeClient.Current.CurrentActivationData.GrainInstance as Grain<IGrainState>;
            if (grain == null)
            {
                throw new NullReferenceException("No GrainInstance has been set up.");
            }
            await grain.Storage.ReadStateAsync();
        }

        /// <summary>
        /// Async method to cause write of the current grain state data into backing store.
        /// </summary>
        [Obsolete("Use Grain.Storage.WriteStateAsync method instead.")]
        public async Task WriteStateAsync()
        {
            var grain = RuntimeClient.Current.CurrentActivationData.GrainInstance as Grain<IGrainState>;
            if (grain == null)
            {
                throw new NullReferenceException("No GrainInstance has been set up.");
            }
            await grain.Storage.WriteStateAsync();
        }

        /// <summary>
        /// Async method to cause write of the current grain state data into backing store.
        /// </summary>
        [Obsolete("Use Grain.Storage.ClearStateAsync method instead.")]
        public async Task ClearStateAsync()
        {
            var grain = RuntimeClient.Current.CurrentActivationData.GrainInstance as Grain<IGrainState>;
            if (grain == null)
            {
                throw new NullReferenceException("No GrainInstance has been set up.");
            }
            await grain.Storage.ClearStateAsync();
        }
        #endregion

        #region IGrainState properties & methods

        /// <summary>
        /// Opaque value set by the storage provider representing an 'Etag' setting for the last time the state data was read from backing store.
        /// </summary>
        public string Etag { get; set; }

        /// <summary>
        /// Converts this property bag into a dictionary.
        /// Overridded with type-specific implementation in generated code.
        /// </summary>
        /// <returns>A Dictionary from string property name to property value.</returns>
        public virtual IDictionary<string, object> AsDictionary()
        {
            var result = new Dictionary<string, object>();
            return result;
        }

        /// <summary>
        /// Populates this property bag from a dictionary.
        /// Overridded with type-specific implementation in generated code.
        /// </summary>
        /// <param name="values">The Dictionary from string to object that contains the values
        /// for this property bag.</param>
        public virtual void SetAll(IDictionary<string, object> values)
        {
            // Nothing to do here. 
            // All relevant implementation logic for handling application data will be in sub-class.
            // All system data is handled by SetAllInternal method, which calls this.
        }
        #endregion
    }
 }