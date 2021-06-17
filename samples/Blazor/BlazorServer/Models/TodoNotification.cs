using Orleans.Concurrency;
using System;

namespace BlazorServer.Models
{
    [Immutable]
    [Serializable]
    public class TodoNotification
    {
        public TodoNotification(Guid itemKey, TodoItem item)
        {
            ItemKey = itemKey;
            Item = item;
        }

        public Guid ItemKey { get; }
        public TodoItem Item { get; }
    }
}