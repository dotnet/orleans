﻿using Orleans.Concurrency;
using System;

namespace Sample.Grains.Models
{
    [Immutable]
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