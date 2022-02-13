namespace Orleans.Runtime
{
    /// <summary> Severity levels for log messages. </summary>
    public enum Severity
    {
        Off = 0,
        Error = 1,
        Warning = 2,
        Info = 3,
        Verbose = 4,
        Verbose2 = Verbose + 1,
        Verbose3 = Verbose + 2
    }
}
