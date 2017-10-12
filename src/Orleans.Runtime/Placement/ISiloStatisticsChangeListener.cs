namespace Orleans.Runtime
{
    internal interface ISiloStatisticsChangeListener
    {
        /// <summary>
        /// Receive notification when new statistics data arrives.
        /// </summary>
        /// <param name="updatedSilo">Updated silo.</param>
        /// <param name="newStats">New Silo statistics.</param>
        void SiloStatisticsChangeNotification(SiloAddress updatedSilo, SiloRuntimeStatistics newStats);

        void RemoveSilo(SiloAddress removedSilo);
    }
}
