
namespace Orleans.Hosting
{
    public class AdoNetOptions
    {
        /// <summary>
        /// When using ADO, identifies the underlying data provider for liveness and reminders. This three-part naming syntax is also used 
        /// when creating a new factory and for identifying the provider in an application configuration file so that the provider name, 
        /// along with its associated connection string, can be retrieved at run time. https://msdn.microsoft.com/en-us/library/dd0w4a2z%28v=vs.110%29.aspx
        /// In order to override this value for reminders set <see cref="AdoInvariantForReminders"/> 
        /// </summary>
        public string Invariant { get; set; }

        /// <summary>
        /// Set this property to override <see cref="AdoInvariant"/> for reminders.
        /// </summary>
        public string InvariantForReminders { get; set; }
    }
}
