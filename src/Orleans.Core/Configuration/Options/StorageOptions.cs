
namespace Orleans.Hosting
{
    public class StorageOptions
    {
        public string DataConnectionString { get; set; }

        /// <summary>
        /// Set this property to override <see cref="DataConnectionString"/> for reminders.
        /// </summary>
        public string DataConnectionStringForReminders { get; set; }
    }
}
