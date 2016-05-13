using System;
using System.Threading.Tasks;

using Orleans;
using Orleans.Providers;

using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Builders;

namespace Samples.StorageProviders
{
    /// <summary>
    /// A MongoDB storage provider.
    /// </summary>
    /// <remarks>
    /// The storage provider should be included in a deployment by adding this line to the Orleans server configuration file:
    /// 
    ///     <Provider Type="Samples.StorageProviders.MongoDBStorage" Name="MongoDBStore" Database="db-name" ConnectionString="mongodb://YOURHOSTNAME:27017/" />
    ///
    /// and this line to any grain that uses it:
    /// 
    ///     [StorageProvider(ProviderName = "MongoDBStore")]
    /// 
    /// The name 'MongoDBStore' is an arbitrary choice.
    /// </remarks>
    public class MongoDBStorage : BaseJSONStorageProvider
    {
        /// <summary>
        /// Database connection string
        /// </summary>
        public string ConnectionString { get; set; }

        /// <summary>
        /// Database name
        /// </summary>
        public string Database { get; set; }

        /// <summary>
        /// Initializes the storage provider.
        /// </summary>
        /// <param name="name">The name of this provider instance.</param>
        /// <param name="providerRuntime">A Orleans runtime object managing all storage providers.</param>
        /// <param name="config">Configuration info for this provider instance.</param>
        /// <returns>Completion promise for this operation.</returns>
        public override Task Init(string name, IProviderRuntime providerRuntime, IProviderConfiguration config)
        {
            this.Name = name;
            this.ConnectionString = config.Properties["ConnectionString"];
            this.Database = config.Properties["Database"];
            if (string.IsNullOrWhiteSpace(ConnectionString)) throw new ArgumentException("ConnectionString property not set");
            if (string.IsNullOrWhiteSpace(Database)) throw new ArgumentException("Database property not set");
            DataManager = new GrainStateMongoDataManager(Database, ConnectionString);
            return base.Init(name, providerRuntime, config);
        }
    }

    /// <summary>
    /// Interfaces with a MongoDB database driver.
    /// </summary>
    internal class GrainStateMongoDataManager : IJSONStateDataManager
    {
        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="connectionString">A database name.</param>
        /// <param name="databaseName">A MongoDB database connection string.</param>
        public GrainStateMongoDataManager(string databaseName, string connectionString)
        {
            MongoClient client = new MongoClient(connectionString);
            _dbServer = client.GetServer();
            _dbServer.Connect();
            _database = _dbServer.GetDatabase(databaseName);
        }

        /// <summary>
        /// Deletes a file representing a grain state object.
        /// </summary>
        /// <param name="collectionName">The type of the grain state object.</param>
        /// <param name="key">The grain id string.</param>
        /// <returns>Completion promise for this operation.</returns>
        public Task Delete(string collectionName, string key)
        {
            var collection = GetCollection(collectionName);
            if (collection == null)
                return TaskDone.Done;

            var query = Query.EQ("key", key);
            collection.FindAndRemove(query, SortBy.Ascending());

            return TaskDone.Done;
        }

        /// <summary>
        /// Reads a file representing a grain state object.
        /// </summary>
        /// <param name="collectionName">The type of the grain state object.</param>
        /// <param name="key">The grain id string.</param>
        /// <returns>Completion promise for this operation.</returns>
        public Task<string> Read(string collectionName, string key)
        {
            var collection = GetCollection(collectionName);
            if (collection == null)
                return Task.FromResult<string>(null);

            var query = Query.EQ("key", key);
            var existing = collection.FindOne(query);

            if (existing == null)
                return Task.FromResult<string>(null);

            existing.Remove("_id");
            existing.Remove("key");

            var strwrtr = new System.IO.StringWriter();
            var writer = new MongoDB.Bson.IO.JsonWriter(strwrtr, new MongoDB.Bson.IO.JsonWriterSettings());
            MongoDB.Bson.Serialization.BsonSerializer.Serialize<BsonDocument>(writer, existing);

            return Task.FromResult(strwrtr.ToString());
        }

        /// <summary>
        /// Writes a file representing a grain state object.
        /// </summary>
        /// <param name="collectionName">The type of the grain state object.</param>
        /// <param name="key">The grain id string.</param>
        /// <param name="entityData">The grain state data to be stored./</param>
        /// <returns>Completion promise for this operation.</returns>
        public Task Write(string collectionName, string key, string entityData)
        {
            var collection = GetOrCreateCollection(collectionName);

            var query = Query.EQ("key", key);
            var existing = collection.FindOne(query);

            var doc = MongoDB.Bson.Serialization.BsonSerializer.Deserialize<BsonDocument>(entityData);
            doc["key"] = key;

            if ( existing == null )
            {
                collection.Insert(doc);
            }
            else
            {
                doc["_id"] = existing["_id"];
                collection.Update(query, Update.Replace(doc));
            }

            return TaskDone.Done;
        }

        /// <summary>
        /// Gets a collection from the MongoDB database.
        /// </summary>
        /// <param name="name">The name of the collection.</param>
        /// <returns></returns>
        private MongoCollection<BsonDocument> GetCollection(string name)
        {
            return _database.GetCollection(name);
        }

        /// <summary>
        /// Gets a collection from the MongoDB database and creates it if it
        /// does not already exist.
        /// </summary>
        /// <param name="name">The name of the collection.</param>
        /// <returns></returns>
        private MongoCollection<BsonDocument> GetOrCreateCollection(string name)
        {
            var collection = _database.GetCollection(name);
            if (collection == null)
            {
                _database.CreateCollection(name);
                collection = _database.GetCollection(name);
            }
            return collection;
        }

        /// <summary>
        /// Clean up.
        /// </summary>
        public void Dispose()
        {
            if (_dbServer != null)
                _dbServer.Disconnect();
            _dbServer = null;
        }

        private MongoServer _dbServer;
        private readonly MongoDatabase _database;

    }
}
