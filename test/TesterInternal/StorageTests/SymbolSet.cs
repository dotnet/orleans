using System;
using System.Collections.Generic;


namespace UnitTests.StorageTests.Relational
{
    /// <summary>
    /// Embodies a set of character encoding ranges.
    /// </summary>
    /// <remarks>Contains some sets of symbols used to create character encodings. See for more at https://en.wikipedia.org/wiki/Character_encoding.</remarks>
    public class SymbolSet
    {
        /// <summary>
        /// Latin-1 with characters only (no punctuation, numbers etc.).
        /// </summary>
        public static IList<Range<int>> Latin1 { get; } = new List<Range<int>>(new[]
        {
            new Range<int>(0x0041, 0x005A),
            new Range<int>(0x0061, 0x007A),
            new Range<int>(0x00C0, 0x00D6),
            new Range<int>(0x00D8, 0x00F6),
            new Range<int>(0x00F8, 0x00FF),
        });

        /// <summary>
        /// Cyrillic character set.
        /// </summary>
        public static IList<Range<int>> Cyrillic { get; } = new List<Range<int>>(new[] { new Range<int>(0x0400, 0x04FF) });

        /// <summary>
        /// Hebrew character set.
        /// </summary>
        public static IList<Range<int>> Hebrew { get; } = new List<Range<int>>(new[] { new Range<int>(0x05D0, 0x05EA) });

        /// <summary>
        /// Unicode emoticons.
        /// </summary>
        public static IList<Range<int>> Emoticons { get; } = new List<Range<int>>(new[] { new Range<int>(0x1F600, 0x1F64F) });

        /// <summary>
        /// Dingbats characters.
        /// </summary>
        public static IList<Range<int>> Dingbats { get; } = new List<Range<int>>(new[] { new Range<int>(0x2700, 0x27BF) });

        /// <summary>
        /// The set of symbols as defined by this set.
        /// </summary>
        public IList<Range<int>> SetRanges { get; }

        /// <summary>
        /// A constructor.
        /// </summary>
        /// <param name="setRanges">A set of symbol ranges to collect the symbols in this collection.</param>
        public SymbolSet(IEnumerable<Range<int>> setRanges)
        {
            if(setRanges == null)
            {
                throw new ArgumentNullException(nameof(setRanges));
            }

            SetRanges = new List<Range<int>>(setRanges);
        }
    }
}
