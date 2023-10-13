//*********************************************************
//    Copyright (c) Microsoft. All rights reserved.
//    
//    Apache 2.0 License
//    
//    You may obtain a copy of the License at
//    http://www.apache.org/licenses/LICENSE-2.0
//    
//    Unless required by applicable law or agreed to in writing, software 
//    distributed under the License is distributed on an "AS IS" BASIS, 
//    WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or 
//    implied. See the License for the specific language governing 
//    permissions and limitations under the License.
//
//*********************************************************

namespace Samples.StorageProviders
{
    /// <summary>
    /// Orleans storage provider implementation for file-backed stores.
    /// </summary>
    /// <remarks>
    /// The storage provider should be included in a deployment by adding this line to the Orleans server configuration file:
    /// 
    ///     <Provider Type="Samples.StorageProviders.OrleansFileStorage" Name="FileStore" RooDirectory="SOME FILE PATH" />
    ///
    /// and this line to any grain that uses it:
    /// 
    ///     [Orleans.Providers.StorageProvider(ProviderName = "FileStore")]
    /// 
    /// The name 'FileStore' is an arbitrary choice.
    /// 
    /// Note that unless the root directory path is a network path available to all silos in a deployment, grain state
    /// will not transport from one silo to another.
    /// </remarks>
    public class OrleansFileStorage : BaseJSONStorageProvider
    {
        public OrleansFileStorage(string rootDirectory)
        {
            this.RootDirectory = rootDirectory;
            if (string.IsNullOrWhiteSpace(RootDirectory)) throw new ArgumentException("RootDirectory property not set");
            DataManager = new GrainStateFileDataManager(RootDirectory);
        }

        /// <summary>
        /// The directory path, relative to the host of the silo. Set from
        /// configuration data during initialization.
        /// </summary>
        public string RootDirectory { get; set; }
    }

    /// <summary>
    /// Interfaces with the file system.
    /// </summary>
    internal class GrainStateFileDataManager : IJSONStateDataManager 
    {
        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="storageDirectory">A path relative to the silo host process' working directory.</param>
        public GrainStateFileDataManager(string storageDirectory)
        {
            directory = new DirectoryInfo(storageDirectory);
            if (!directory.Exists)
                directory.Create();
        }

        /// <summary>
        /// Deletes a file representing a grain state object.
        /// </summary>
        /// <param name="collectionName">The type of the grain state object.</param>
        /// <param name="key">The grain id string.</param>
        /// <returns>Completion promise for this operation.</returns>
        public Task Delete(string collectionName, string key)
        {
            FileInfo fileInfo = GetStorageFilePath(collectionName, key);

            if (fileInfo.Exists)
                fileInfo.Delete();

            return Task.CompletedTask;
        }

        /// <summary>
        /// Reads a file representing a grain state object.
        /// </summary>
        /// <param name="collectionName">The type of the grain state object.</param>
        /// <param name="key">The grain id string.</param>
        /// <returns>Completion promise for this operation.</returns>
        public async Task<string> Read(string collectionName, string key)
        {
            FileInfo fileInfo = GetStorageFilePath(collectionName, key);

            if (!fileInfo.Exists)
                return null;

            using (var stream = fileInfo.OpenText())
            {
                return await stream.ReadToEndAsync();
            }
        }

        /// <summary>
        /// Writes a file representing a grain state object.
        /// </summary>
        /// <param name="collectionName">The type of the grain state object.</param>
        /// <param name="key">The grain id string.</param>
        /// <param name="entityData">The grain state data to be stored./</param>
        /// <returns>Completion promise for this operation.</returns>
        public async Task Write(string collectionName, string key, string entityData)
        {
            FileInfo fileInfo = GetStorageFilePath(collectionName, key);

            using (var stream = new StreamWriter(fileInfo.Open(FileMode.Create, FileAccess.Write)))
            {
                await stream.WriteAsync(entityData);
            }
        }

        public void Dispose()
        {
        }

        /// <summary>
        /// Returns the file path for storing that data with these keys.
        /// </summary>
        /// <param name="collectionName">The type of the grain state object.</param>
        /// <param name="key">The grain id string.</param>
        /// <returns>File info for this storage data file.</returns>
        private FileInfo GetStorageFilePath(string collectionName, string key)
        {
            string fileName = (key + "." + collectionName).Replace('/', '_');
            string path = Path.Combine(directory.FullName, fileName);
            return new FileInfo(path);
        }

        private readonly DirectoryInfo directory;
    }
}
