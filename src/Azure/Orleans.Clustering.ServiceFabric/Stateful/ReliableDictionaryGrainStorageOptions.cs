namespace Orleans.Clustering.ServiceFabric.Stateful
{
    public class ReliableDictionaryGrainStorageOptions
    {
        public string StateName { get; set; }

        /// <summary>
        /// Stage of silo lifecycle where storage should be initialized.  Storage must be initialzed prior to use.
        /// </summary>
        public int InitStage { get; set; } = ServiceLifecycleStage.ApplicationServices;
    }
}