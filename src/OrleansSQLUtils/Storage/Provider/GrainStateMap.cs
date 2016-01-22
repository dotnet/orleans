using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;

namespace Orleans.SqlUtils.StorageProvider
{
    /// <summary>
    /// Map of grain state to SQL tables
    /// </summary>
    internal class GrainStateMap
    {
        /// <summary>
        /// No need to use ConcurrentDictionary since it is instantiated only once and then does only lookups
        /// </summary>
        private readonly Dictionary<string, GrainStateMapEntry> _map = new Dictionary<string, GrainStateMapEntry>();

        internal GrainStateMapEntry For(string grainType)
        {
            GrainStateMapEntry mapEntry;
            if (!_map.TryGetValue(grainType, out mapEntry))
                throw new ArgumentOutOfRangeException(string.Format("GrainStateMap has no registration for {0}", grainType));
            return mapEntry;
        }

        internal GrainStateMap Register(string grainType,
            Action<SqlCommand, DataTable> prepareReadSqlCommand,
            Func<SqlDataReader, object> createState,
            Func<IEnumerable<WriteEntry>, DataTable> prepareDataTable, 
            Action<SqlCommand, DataTable> prepareUpsertSqlCommand)
        {
            _map.Add(grainType,
                new GrainStateMapEntry(prepareReadSqlCommand, createState, prepareDataTable, prepareUpsertSqlCommand)
                );
            return this;
        }

        /// <summary>
        /// Registers a map
        /// </summary>
        /// <typeparam name="TGrainType">Grain type</typeparam>
        /// <param name="prepareReadSqlCommand">Action to prepare a read command</param>
        /// <param name="createState">Function to populate a grain state property bag</param>
        /// <param name="prepareDataTable">Function to prepare a SQL table</param>
        /// <param name="prepareUpsertSqlCommand">Action to prepare an upsert command</param>
        /// <returns></returns>
        public GrainStateMap Register<TGrainType>(
            Action<SqlCommand, DataTable> prepareReadSqlCommand,
            Func<SqlDataReader, object> createState,
            Func<IEnumerable<WriteEntry>, DataTable> prepareDataTable,
            Action<SqlCommand, DataTable> prepareUpsertSqlCommand)
        {
            Register(
                typeof (TGrainType).FullName, 
                prepareReadSqlCommand, createState, 
                prepareDataTable, prepareUpsertSqlCommand
                );
            return this;
        }
    }

    internal class GrainStateMapEntry
    {
        private readonly Action<SqlCommand, DataTable> _prepareReadSqlCommand;
        private readonly Action<SqlCommand, DataTable> _prepareUpsertSqlCommand;
        private readonly Func<IEnumerable<WriteEntry>, DataTable> _prepareDataTable;
        private readonly Func<SqlDataReader, object> _createState;


        internal GrainStateMapEntry(
            Action<SqlCommand, DataTable> prepareReadSqlCommand,
            Func<SqlDataReader, object> createState,
            Func<IEnumerable<WriteEntry>, DataTable> prepareDataTable,
            Action<SqlCommand, DataTable> prepareUpsertSqlCommand
            )
        {
            _prepareReadSqlCommand = prepareReadSqlCommand;
            _createState = createState;
            _prepareUpsertSqlCommand = prepareUpsertSqlCommand;
            _prepareDataTable = prepareDataTable;
        }

        public void PrepareReadSqlCommand(SqlCommand cmd, DataTable data)
        {
            _prepareReadSqlCommand(cmd, data);
        }

        public void PrepareUpsertSqlCommand(SqlCommand cmd, DataTable data)
        {
            _prepareUpsertSqlCommand(cmd, data);
        }

        public DataTable PrepareDataTable(IEnumerable<WriteEntry> batch)
        {
            return _prepareDataTable(batch);
        }

        public object CreateState(SqlDataReader reader)
        {
            return _createState(reader);
        }
    }
}
