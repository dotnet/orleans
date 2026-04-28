namespace Orleans.CodeGenerator.Model;

internal readonly record struct SourceLocationModel
{
    public SourceLocationModel(int sourceOrderGroup, string filePath, int position)
    {
        SourceOrderGroup = sourceOrderGroup;
        FilePath = filePath ?? string.Empty;
        Position = position;
    }

    public int SourceOrderGroup { get; }
    public string FilePath { get; }
    public int Position { get; }
}
