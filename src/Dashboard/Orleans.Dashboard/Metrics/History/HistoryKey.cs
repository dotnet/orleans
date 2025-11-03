namespace Orleans.Dashboard.Metrics.History;

internal record struct HistoryKey(string SiloAddress, string Grain, string Method);
