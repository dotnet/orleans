using System.Diagnostics;

namespace OneBoxDeployment.OrleansUtilities
{
    [DebuggerDisplay("ConnectionConfig(Name = {Name}, ConnectionString = {ConnectionString}, AdoNetConstant = {AdoNetConstant})")]
    public sealed class ConnectionConfig
    {
        /// <summary>
        /// The name of the grain storage configuration.
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// The connection string to the storage.
        /// </summary>
        public string ConnectionString { get; set; }

        /// <summary>
        /// This is optional and applies only to ADO.NET.
        /// </summary>
        public string AdoNetConstant { get; set; }
    }
}
