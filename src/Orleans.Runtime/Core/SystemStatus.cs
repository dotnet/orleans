using System;
using System.Diagnostics.CodeAnalysis;

namespace Orleans.Runtime
{
    /// <summary>
    /// System status values and current register
    /// </summary>
    internal sealed class SystemStatus : IEquatable<SystemStatus>
    {
        private enum InternalSystemStatus
        {
            Unknown = 0,
            Creating,
            Created,
            Starting,
            Running,
            Stopping,
            ShuttingDown,
            Terminated,
        };

        /// <summary>Status = Unknown</summary>
        [SuppressMessage("Microsoft.Security", "CA2104:DoNotDeclareReadOnlyMutableReferenceTypes")]
        public static readonly SystemStatus Unknown = new SystemStatus(InternalSystemStatus.Unknown);

        /// <summary>Status = Creating</summary>
        [SuppressMessage("Microsoft.Security", "CA2104:DoNotDeclareReadOnlyMutableReferenceTypes")]
        public static readonly SystemStatus Creating = new SystemStatus(InternalSystemStatus.Creating);

        /// <summary>Status = Created</summary>
        [SuppressMessage("Microsoft.Security", "CA2104:DoNotDeclareReadOnlyMutableReferenceTypes")]
        public static readonly SystemStatus Created = new SystemStatus(InternalSystemStatus.Created);

        /// <summary>Status = Starting</summary>
        [SuppressMessage("Microsoft.Security", "CA2104:DoNotDeclareReadOnlyMutableReferenceTypes")]
        public static readonly SystemStatus Starting = new SystemStatus(InternalSystemStatus.Starting);
        
        /// <summary>Status = Running</summary>
        [SuppressMessage("Microsoft.Security", "CA2104:DoNotDeclareReadOnlyMutableReferenceTypes")]
        public static readonly SystemStatus Running = new SystemStatus(InternalSystemStatus.Running);
        
        /// <summary>Status = Stopping</summary>
        [SuppressMessage("Microsoft.Security", "CA2104:DoNotDeclareReadOnlyMutableReferenceTypes")]
        public static readonly SystemStatus Stopping = new SystemStatus(InternalSystemStatus.Stopping);
        
        /// <summary>Status = ShuttingDown</summary>
        [SuppressMessage("Microsoft.Security", "CA2104:DoNotDeclareReadOnlyMutableReferenceTypes")]
        public static readonly SystemStatus ShuttingDown = new SystemStatus(InternalSystemStatus.ShuttingDown);

        /// <summary>Status = Terminated</summary>
        [SuppressMessage("Microsoft.Security", "CA2104:DoNotDeclareReadOnlyMutableReferenceTypes")]
        public static readonly SystemStatus Terminated = new SystemStatus(InternalSystemStatus.Terminated);

        private readonly InternalSystemStatus value;
        private SystemStatus(InternalSystemStatus name) { this.value = name; }

        /// <see cref="object.ToString"/>
        public override string ToString() { return this.value.ToString(); }
        /// <see cref="object.GetHashCode"/>
        public override int GetHashCode() { return this.value.GetHashCode(); }
        /// <see cref="object.Equals(object)"/>
        public override bool Equals(object obj) { var ss = obj as SystemStatus; return ss != null && this.Equals(ss); }
        /// <see cref="IEquatable{T}.Equals(T)"/>
        public bool Equals(SystemStatus other) { return (other != null) && this.value.Equals(other.value); }
    }
}
