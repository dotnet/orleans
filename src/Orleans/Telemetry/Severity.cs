namespace Orleans.Runtime
{
    /// <summary> Severity levels for log messages. </summary>
    public enum Severity
    {
        Off = 0,
        Critical = 1,
        Error = 2,
        Warning = 3,
        Info = 4,
        Verbose = 5,
        Verbose2 = Verbose + 1,
        Verbose3 = Verbose + 2
    }
}
