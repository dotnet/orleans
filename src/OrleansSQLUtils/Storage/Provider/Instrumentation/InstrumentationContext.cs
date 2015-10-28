namespace Orleans.SqlUtils.StorageProvider.Instrumentation
{
    public class InstrumentationContext
    {
        private static readonly StorageProvidersInstrumentationManager InstrumentationManager;
        static InstrumentationContext()
        {
            InstrumentationManager = new StorageProvidersInstrumentationManager(true);
        }


        public static void Reset()
        {
            InstrumentationManager.OpenConnections.Counter.ResetCounter();
            InstrumentationManager.WritesPending.Counter.ResetCounter();
            InstrumentationManager.WriteErrors.Counter.ResetCounter();
            InstrumentationManager.WritesPostFailures.Counter.ResetCounter();
            InstrumentationManager.ReadsPending.Counter.ResetCounter();
            InstrumentationManager.ReadPostFailures.Counter.ResetCounter();
            InstrumentationManager.ReadErrors.Counter.ResetCounter();
            InstrumentationManager.SqlTransientErrors.Counter.ResetCounter();
        }

        public static void WritePosted()
        {
            InstrumentationManager.WritesPending.Counter.Increment();
        }

        public static void WritesCompleted(int count)
        {
            InstrumentationManager.WritesPending.Counter.DecrementBy(count);
        }

        public static void ReadsCompleted(int count)
        {
            InstrumentationManager.ReadsPending.Counter.DecrementBy(count);
        }

        public static void ReadPosted()
        {
            InstrumentationManager.ReadsPending.Counter.Increment();
        }

        public static void ReadPostFailed()
        {
            InstrumentationManager.ReadPostFailures.Counter.Increment();
        }

        public static void WritePostFailed()
        {
            InstrumentationManager.WritesPostFailures.Counter.Increment();
        }

        public static void SqlTransientErrorOccurred()
        {
            InstrumentationManager.SqlTransientErrors.Counter.Increment();
        }

        public static void ConnectionOpened()
        {
            InstrumentationManager.OpenConnections.Counter.Increment();
        }

        public static void ConnectionClosed()
        {
            InstrumentationManager.OpenConnections.Counter.Decrement();
        }

        public static void ReadErrorOccurred()
        {
            InstrumentationManager.ReadErrors.Counter.Increment();
        }

        public static void WriteErrorOccurred()
        {
            InstrumentationManager.WriteErrors.Counter.Increment();
        }
    }
}