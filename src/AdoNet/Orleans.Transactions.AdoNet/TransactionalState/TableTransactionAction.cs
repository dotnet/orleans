using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans.Transactions.AdoNet.Entity;

namespace Orleans.Transactions.AdoNet.TransactionalState
{
    internal enum TableTransactionActionType
    {

        Add,

        UpdateReplace,

        Delete,
    }

    internal class TableTransactionAction
    {
        public TableTransactionAction(TableTransactionActionType action, StateEntity state) : this(action, state, default)
        {
        }

        public TableTransactionAction(TableTransactionActionType action, StateEntity state,string eTag)
        {
            ActionType = action;
            State = state;
            ETag = eTag;
        }

        public TableTransactionAction(TableTransactionActionType action, KeyEntity key) : this(action, key, key.ETag)
        {
        }

        public TableTransactionAction(TableTransactionActionType action, KeyEntity key,string eTag)
        {
            ActionType = action;
            Key = key;
            ETag = eTag;
        }


        public TableTransactionActionType ActionType { get; }
        public StateEntity State { get; }
        public KeyEntity Key { get; }

        public string ETag { get; set; }

    }
}
