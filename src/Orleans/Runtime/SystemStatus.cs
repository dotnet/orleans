using System;
using System.Diagnostics.CodeAnalysis;

namespace Orleans.Runtime
{
    /// <summary>
    /// System status values and current register
    /// </summary>
    internal sealed class SystemStatus : IEquatable<SystemStatus>
    {
        // Current system status
        public static SystemStatus Current
        {
            get
            {
                // System should always have some status, even if it is Status==Unknown
                return currentStatus ?? (currentStatus = SystemStatus.Unknown);
            }
            set
            {
                // System should always have some status, even if it is Status==Unknown
                if (value == null) value = SystemStatus.Unknown;

                currentStatus = value;

                if (!value.Equals(SystemStatus.Creating)) // don't print Creating because the logger has not been initialzed properly yet.
                {
                    logger.Info(ErrorCode.Runtime_Error_100294, "SystemStatus={0}", value);
                }
            }
        }

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
        }

        private static SystemStatus currentStatus;

        private static readonly Logger logger = LogManager.GetLogger("SystemStatus", LoggerType.Runtime);

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
        
        /// <summary>Status = Shuttingdown</summary>
        [SuppressMessage("Microsoft.Security", "CA2104:DoNotDeclareReadOnlyMutableReferenceTypes")]
        public static readonly SystemStatus ShuttingDown = new SystemStatus(InternalSystemStatus.ShuttingDown);

        /// <summary>Status = Terminated</summary>
        [SuppressMessage("Microsoft.Security", "CA2104:DoNotDeclareReadOnlyMutableReferenceTypes")]
        public static readonly SystemStatus Terminated = new SystemStatus(InternalSystemStatus.Terminated);

        private readonly InternalSystemStatus value;
        private SystemStatus(InternalSystemStatus name) { this.value = name; }

        /// <see cref="Object.ToString"/>
        public override string ToString() { return this.value.ToString(); }
        /// <see cref="Object.GetHashCode"/>
        public override int GetHashCode() { return this.value.GetHashCode(); }
        /// <see cref="Object.Equals(Object)"/>
        public override bool Equals(object obj) { var ss = obj as SystemStatus; return ss != null && this.Equals(ss); }
        /// <see cref="IEquatable{T}.Equals"/>
        public bool Equals(SystemStatus other) { return (other != null) && this.value.Equals(other.value); }
    }
}
