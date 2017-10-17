using System;
using Orleans.Runtime;

namespace Orleans.CodeGeneration
{
    /// <summary>
    /// Abstract base class for all grain proxy factory classes.
    /// </summary>
    /// <remarks>
    /// These methods are used from generated code.
    /// </remarks>
    public static class GrainFactoryBase
    {
        /// <summary>
        /// Check that a grain observer parameter is of the correct underlying concrent type -- either extending from <c>GrainRefereence</c> or <c>Grain</c>
        /// </summary>
        /// <param name="grainObserver">Grain observer parameter to be checked.</param>
        /// <exception cref="ArgumentNullException">If grainObserver is <c>null</c></exception>
        /// <exception cref="NotSupportedException">If grainObserver class is not an appropriate underlying concrete type.</exception>
        public static void CheckGrainObserverParamInternal(IGrainObserver grainObserver)
        {
            if (grainObserver == null)
            {
                throw new ArgumentNullException("grainObserver", "IGrainObserver parameters cannot be null");
            }
            if (grainObserver is GrainReference || grainObserver is Grain)
            {
                // OK
            }
            else
            {
                string errMsg = string.Format("IGrainObserver parameters must be GrainReference or Grain and cannot be type {0}. Did you forget to CreateObjectReference?", grainObserver.GetType());
                throw new NotSupportedException(errMsg);
            }
        }

        #region Utility functions

        internal static void DisallowNullOrWhiteSpaceKeyExtensions(string keyExt)
        {
            if (!string.IsNullOrWhiteSpace(keyExt)) return;

            if (null == keyExt)
            {
                throw new ArgumentNullException("keyExt"); 
            }
            
            throw new ArgumentException("Key extension is empty or white space.", "keyExt");
        }

        #endregion
    }
}
