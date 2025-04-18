using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Orleans.Transactions.AdoNet.TransactionalState
{
    public enum TableTransactionActionType
    {

        Add,

       // UpdateMerge,

        UpdateReplace,

        Delete,

        //UpsertMerge,

        //UpsertReplace
    }

    public class TableTransactionAction
    {
        public TableTransactionAction(TableTransactionActionType action, StateEntity state) : this(action, state, default)
        {
            ActionType = action;
            State = state;
            ETag = state.ETag;
        }

        public TableTransactionAction(TableTransactionActionType action, StateEntity state,string eTag)
        {
            ActionType = action;
            State = state;
            ETag = eTag;
        }

        public TableTransactionAction(TableTransactionActionType action, KeyEntity key) : this(action, key, default)
        {
            ActionType = action;
            Key = key;
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
