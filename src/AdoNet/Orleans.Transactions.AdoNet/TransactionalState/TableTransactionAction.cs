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
        public TableTransactionAction(TableTransactionActionType action, IEntity tableEntity) 
        {
            this.ActionType = action;
            this.TableEntity = tableEntity;
        }


        public TableTransactionActionType ActionType { get; }
        public IEntity TableEntity { get; }

    }
}
