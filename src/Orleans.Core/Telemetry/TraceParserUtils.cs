using System.Collections.Generic;
using System.Text;

namespace Orleans.Runtime
{
    internal static class TraceParserUtils
    {
        public static string PrintProperties(string message, IDictionary<string, string> properties)
        {
            if (properties == null || properties.Keys.Count == 0)
                return message;

            var sb = new StringBuilder(message + " - Properties:");
            sb.Append(" ");
            sb.Append("{");

            foreach (var key in properties.Keys)
            {
                sb.Append(" ");
                sb.Append(key);
                sb.Append(" : ");
                sb.Append(properties[key]);
                sb.Append(",");
            }
            sb.Remove(sb.Length - 1, 1);
            sb.Append(" ");
            sb.Append("}");
            return sb.ToString();
        }
    }
}
