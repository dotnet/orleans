using System;

namespace Orleans.Threading
{
    // Instance that can be attached to custom object in order to track it's end of life
    internal class GCObserver
    {
        private readonly Action onFinalization;

        /// <param name="onFinalization">Action that will be executed on finalization</param>
        public GCObserver(Action onFinalization)
        {
            this.onFinalization = onFinalization;
        }

        ~GCObserver()
        {
            try
            {
                onFinalization();
            }
            catch
            {
                // no guarantees here
            }
        }
    }
}