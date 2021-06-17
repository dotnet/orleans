using System;

namespace BlazorWasm.Models
{
    public class TodoItem : IEquatable<TodoItem>
    {
        public Guid Key { get; set; }
        public string Title { get; set; }
        public bool IsDone { get; set; }
        public Guid OwnerKey { get; set; }

        public bool Equals(TodoItem other)
        {
            if (other == null) return false;
            return Key == other.Key
                && Title == other.Title
                && IsDone == other.IsDone
                && OwnerKey == other.OwnerKey;
        }

        /*
        public override int GetHashCode() =>
            HashCode.Combine(Key, Title, IsDone, OwnerKey);
        */
    }
}