namespace Orleans.Runtime
{
    public static class Metric
    {
        public static string CreateCurrentName(string statisticName)
        {
            return statisticName + "." + "Current";
        }
        public static string CreateDeltaName(string statisticName)
        {
            return statisticName + "." + "Delta";
        }
    }
}