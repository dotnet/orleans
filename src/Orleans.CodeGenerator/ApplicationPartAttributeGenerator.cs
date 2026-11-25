using Orleans.CodeGenerator.SyntaxGeneration;

namespace Orleans.CodeGenerator;

internal static class ApplicationPartAttributeGenerator
{
    /// <summary>Returns one assembly-level application part attribute for each named assembly.</summary>
    public static List<string> GenerateAttributes(string applicationPartAttribute, IEnumerable<string> applicationParts)
    {
        var attributes = new List<string>();
        foreach (var assemblyName in applicationParts)
        {
            attributes.Add($"[assembly: {applicationPartAttribute}({CodeText.QuotedText(assemblyName)})]");
        }

        return attributes;
    }
}
