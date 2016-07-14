namespace Orleans.Runtime.MultiClusterNetwork
{
    internal class MultiClusterOracleData 
    {
        private volatile MultiClusterData localData;  // immutable, can read without lock

        private readonly Logger logger;

        internal MultiClusterData Current { get { return localData; } }

        internal MultiClusterOracleData(Logger log)
        {
            logger = log;
            localData = new MultiClusterData();
        }


        public MultiClusterData ApplyDataAndNotify(MultiClusterData data)
        {
            if (data.IsEmpty)
                return data;

            MultiClusterData delta;
            MultiClusterData prev = this.localData;

            this.localData = prev.Merge(data, out delta);

            if (logger.IsVerbose2)
                logger.Verbose2("ApplyDataAndNotify: delta {0}", delta);

            if (delta.IsEmpty)
                return delta;

            if (delta.Configuration != null)
            {
                // notify configuration listeners of change
                // code will be added in separate PR
            }

            return delta;
        }
    }
}
