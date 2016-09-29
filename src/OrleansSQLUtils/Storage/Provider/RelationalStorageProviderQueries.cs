using System;


namespace Orleans.Storage
{
    /// <summary>
    /// A container class for the queries currently used by the <see cref="AdoNetStorageProvider"/>.
    /// </summary>
    /// <remarks>This is provided as a separate entity in order to make these dynamically updatable.</remarks>
    public class RelationalStorageProviderQueries
    {
        /// <summary>
        /// The clause to write to the storage.
        /// </summary>
        public string WriteToStorage { get; }

        /// <summary>
        /// The clause to read from the storage.
        /// </summary>
        public string ReadFromStorage { get; set; }

        /// <summary>
        /// The clause to clear the storage.
        /// </summary>
        public string ClearState { get; set; }


        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="writeToStorage">The clause to write to a storage.</param>
        /// <param name="readFromStorage">The clause to read from a storage.</param>
        /// <param name="clearState">The clause to clear the storage.</param>
        public RelationalStorageProviderQueries(string writeToStorage, string readFromStorage, string clearState)
        {
            if(writeToStorage == null)
            {
                throw new ArgumentNullException(nameof(writeToStorage));
            }

            if(readFromStorage == null)
            {
                throw new ArgumentNullException(nameof(readFromStorage));
            }

            if(clearState == null)
            {
                throw new ArgumentNullException(nameof(clearState));
            }

            WriteToStorage = writeToStorage;
            ReadFromStorage = readFromStorage;
            ClearState = clearState;
        }
    }
}
