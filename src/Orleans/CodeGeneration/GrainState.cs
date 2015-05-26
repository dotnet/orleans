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

        #region IGrainState properties & methods

        /// <summary>
        /// Opaque value set by the storage provider representing an 'Etag' setting for the last time the state data was read from backing store.
        /// </summary>
        public string Etag { get; set; }

        /// <summary>
        /// Async method to cause refresh of the current grain state data from backing store.
        /// Any previous contents of the grain state data will be overwritten.
        /// </summary>
        public async Task ReadStateAsync()
        {
            const string what = "ReadState";
            Stopwatch sw = Stopwatch.StartNew();
            // The below is the worng way to create GrainReference.
            // IAddressable.AsReference is also a wrong way to get GrainReference.
            // Both are weakly types and lose the actual strongly typed Grain Reference.
            GrainReference grainRef = RuntimeClient.Current.CurrentActivationData.GrainReference;
            IStorageProvider storage = GetCheckStorageProvider(what);
            try
            {
                await storage.ReadStateAsync(grainTypeName, grainRef, this);
                StorageStatisticsGroup.OnStorageRead(storage, grainTypeName, grainRef, sw.Elapsed);
            }
            catch (Exception exc)
            {
                StorageStatisticsGroup.OnStorageReadError(storage, grainTypeName, grainRef);
                string errMsg = MakeErrorMsg(what, grainRef, exc);

                storage.Log.Error((int)ErrorCode.StorageProvider_ReadFailed, errMsg, exc);
                throw new OrleansException(errMsg, exc);
            }
            finally
            {
                sw.Stop();
            }
        }

        /// <summary>
        /// Async method to cause write of the current grain state data into backing store.
        /// </summary>
        public async Task WriteStateAsync()
        {
            const string what = "WriteState";
            Stopwatch sw = Stopwatch.StartNew();
            GrainReference grainRef = RuntimeClient.Current.CurrentActivationData.GrainReference;
            IStorageProvider storage = GetCheckStorageProvider(what);

            Exception errorOccurred;
            try
            {
                await storage.WriteStateAsync(grainTypeName, grainRef, this);
                StorageStatisticsGroup.OnStorageWrite(storage, grainTypeName, grainRef, sw.Elapsed);
                errorOccurred = null;
            }
            catch (Exception exc)
            {
                errorOccurred = exc;
            }
            // Note, we can't do this inside catch block above, because await is not permitted there.
            if (errorOccurred != null)
            {
                StorageStatisticsGroup.OnStorageWriteError(storage, grainTypeName, grainRef);
                string errMsgToLog = MakeErrorMsg(what, grainRef, errorOccurred);

                storage.Log.Error((int)ErrorCode.StorageProvider_WriteFailed, errMsgToLog, errorOccurred);
                errorOccurred = new OrleansException(errMsgToLog, errorOccurred);

#if REREAD_STATE_AFTER_WRITE_FAILED
            // Force rollback to previously stored state
            try
            {
                sw.Restart();
                storage.Log.Warn((int)ErrorCode.StorageProvider_ForceReRead, "Forcing re-read of last good state for grain Type={0}", grainTypeName);
                await storage.ReadStateAsync(grainTypeName, grainId, this);
                StorageStatisticsGroup.OnStorageRead(storage, grainTypeName, grainId, sw.Elapsed);
            }
            catch (Exception exc)
            {
                StorageStatisticsGroup.OnStorageReadError(storage, grainTypeName, grainId);
                // Should we ignore this secondary error, and just return the original one?
                errMsg = MakeErrorMsg("re-read state from store after write error", grainId, exc);
                errorOccurred = new OrleansException(errMsg, exc);
            }
#endif
            }
            sw.Stop();
            if (errorOccurred != null)
            {
                throw errorOccurred;
            }
        }

        /// <summary>
        /// Async method to cause write of the current grain state data into backing store.
        /// </summary>
        public async Task ClearStateAsync()
        {
            const string what = "ClearState";
            Stopwatch sw = Stopwatch.StartNew();
            GrainReference grainRef = RuntimeClient.Current.CurrentActivationData.GrainReference;
            IStorageProvider storage = GetCheckStorageProvider(what);
            try
            {
                // Clear (most likely Delete) state from external storage
                await storage.ClearStateAsync(grainTypeName, grainRef, this);
                // Null out the in-memory copy of the state
                SetAll(null);
                // Update counters
                StorageStatisticsGroup.OnStorageDelete(storage, grainTypeName, grainRef, sw.Elapsed);
            }
            catch (Exception exc)
            {
                StorageStatisticsGroup.OnStorageDeleteError(storage, grainTypeName, grainRef);
                string errMsg = MakeErrorMsg(what, grainRef, exc);

                storage.Log.Error((int)ErrorCode.StorageProvider_DeleteFailed, errMsg, exc);
                throw new OrleansException(errMsg, exc);
            }
            finally
            {
                sw.Stop();
            }
        }

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

        private string MakeErrorMsg(string what, GrainReference grainReference, Exception exc)
        {
            HttpStatusCode httpStatusCode;
            string errorCode;
            AzureStorageUtils.EvaluateException(exc, out httpStatusCode, out errorCode, true);
            return string.Format("Error from storage provider during {0} for grain Type={1} Pk={2} Id={3} Error={4}"  + Environment.NewLine + " {5}",
                what, grainTypeName, grainReference.GrainId.ToDetailedString(), grainReference, errorCode, TraceLogger.PrintException(exc));
        }

        private IStorageProvider GetCheckStorageProvider(string what)
        {
            IStorageProvider storage = RuntimeClient.Current.CurrentStorageProvider;
            if (storage == null)
            {
                throw new OrleansException(string.Format(
                    "Cannot {0} - No storage provider configured for grain Type={1}", what, grainTypeName));
            }
            return storage;
        }
    }
 }