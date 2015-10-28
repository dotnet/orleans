using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;

namespace Orleans.SqlUtils.StorageProvider
{
    public class GrainStateMap
    {
        /// <summary>
        /// No need to use ConcurrentDictionary since it is instantiated only once and then does only lookups
        /// </summary>
        private readonly Dictionary<string, GrainStateMapEntry> _map = new Dictionary<string, GrainStateMapEntry>();

        public GrainStateMapEntry For(string grainType)
        {
            GrainStateMapEntry mapEntry;
            if (!_map.TryGetValue(grainType, out mapEntry))
                throw new ArgumentOutOfRangeException(string.Format("GrainStateMap has no registration for {0}", grainType));
            return mapEntry;
        }

        public GrainStateMap Register(string grainType,
            Action<SqlCommand, DataTable> prepareReadSqlCommand,
            Func<SqlDataReader, IDictionary<string, object>> createState,
            Func<IEnumerable<WriteEntry>, DataTable> prepareDataTable, 
            Action<SqlCommand, DataTable> prepareUpsertSqlCommand)
        {
            _map.Add(grainType,
                new GrainStateMapEntry(prepareReadSqlCommand, createState, prepareDataTable, prepareUpsertSqlCommand)
                );
            return this;
        }

        public GrainStateMap Register<TGrainType>(
            Action<SqlCommand, DataTable> prepareReadSqlCommand,
            Func<SqlDataReader, IDictionary<string, object>> createState,
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

    public class GrainStateMapEntry
    {
        private readonly Action<SqlCommand, DataTable> _prepareReadSqlCommand;
        private readonly Action<SqlCommand, DataTable> _prepareUpsertSqlCommand;
        private readonly Func<IEnumerable<WriteEntry>, DataTable> _prepareDataTable;
        private readonly Func<SqlDataReader, IDictionary<string, object>> _createState;


        internal GrainStateMapEntry(
            Action<SqlCommand, DataTable> prepareReadSqlCommand,
            Func<SqlDataReader, IDictionary<string, object>> createState,
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

        public IDictionary<string, object> CreateState(SqlDataReader reader)
        {
            return _createState(reader);
        }
    }
}
