using Orleans;
using Orleans.Runtime;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;


namespace Orleans.Storage
{
    /// <summary>
    /// <see cref="IStorageHasherPicker"/>.
    /// </summary>
    public class StorageHasherPicker: IStorageHasherPicker
    {
        /// <summary>
        /// <see cref="IStorageHasherPicker.HashProviders"/>.
        /// </summary>
        public ICollection<IHasher> HashProviders { get; }


        /// <summary>
        /// A constructor.
        /// </summary>
        /// <param name="hashProviders">The hash providers this picker uses.</param>
        public StorageHasherPicker(IEnumerable<IHasher> hashProviders)
        {
            if(hashProviders == null)
            {
                throw new ArgumentNullException(nameof(hashProviders));
            }

            HashProviders = new Collection<IHasher>(new List<IHasher>(hashProviders));
        }


        /// <summary>
        /// <see cref="IStorageHasherPicker.PickHasher(string, string, string, GrainReference, IGrainState, string)"/>.
        /// </summary>
        public IHasher PickHasher(string serviceId, string storageProviderInstanceName, string grainType, GrainReference grainReference, IGrainState grainState, string tag = null)
        {
            return HashProviders.FirstOrDefault();
        }
    }
}
