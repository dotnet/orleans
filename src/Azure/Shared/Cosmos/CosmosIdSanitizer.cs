#if ORLEANS_CLUSTERING
namespace Orleans.Clustering.Cosmos;
#elif ORLEANS_PERSISTENCE
namespace Orleans.Persistence.Cosmos;
#elif ORLEANS_REMINDERS
namespace Orleans.Reminders.Cosmos;
#elif ORLEANS_STREAMING
namespace Orleans.Streaming.Cosmos;
#elif ORLEANS_DIRECTORY
namespace Orleans.GrainDirectory.Cosmos;
#else
// No default namespace intentionally to cause compile errors if something is not defined
#endif

internal static class CosmosIdSanitizer
{
    private const char EscapeChar = '~';
    private static ReadOnlySpan<char> SanitizedCharacters => new[] { '/', '\\', '?', '#', SeparatorChar, EscapeChar };
    private static ReadOnlySpan<char> ReplacementCharacters => new[] { '0', '1', '2', '3', '4', '5' };

    public const char SeparatorChar = '_';

    public static string Sanitize(string input)
    {
        var count = 0;
        foreach (var c in input)
        {
            var charId = SanitizedCharacters.IndexOf(c);
            if (charId >= 0)
            {
                ++count;
            }
        }

        if (count == 0)
        {
            return input;
        }

        return string.Create(input.Length + count, input, static (output, input) =>
        {
            var i = 0;
            foreach (var c in input)
            {
                var charId = SanitizedCharacters.IndexOf(c);
                if (charId < 0)
                {
                    output[i++] = c;
                    continue;
                }

                output[i++] = EscapeChar;
                output[i++] = ReplacementCharacters[charId];
            }
        });
    }

    public static string Unsanitize(string input)
    {
        var count = 0;
        foreach (var c in input)
        {
            if (c == EscapeChar)
            {
                ++count;
            }
        }

        if (count == 0)
        {
            return input;
        }

        return string.Create(input.Length - count, input, static (output, input) =>
        {
            var i = 0;
            var isEscaped = false;
            foreach (var c in input)
            {
                if (isEscaped)
                {
                    var charId = ReplacementCharacters.IndexOf(c);
                    if (charId < 0)
                    {
                        throw new ArgumentException($"Input is not in a valid format: Encountered unsupported escape sequence");
                    }

                    output[i++] = SanitizedCharacters[charId];
                    isEscaped = false;
                }
                else if (c == EscapeChar)
                {
                    isEscaped = true;
                }
                else
                {
                    output[i++] = c;
                }
            }
        });
    }
}