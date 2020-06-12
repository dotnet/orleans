using System;
using System.Globalization;
using System.Text;

namespace Orleans.CodeGenerator.Utilities
{
    internal static class CSharpIdentifier
    {
        // CSharp Spec §2.4.2
        private static bool IsIdentifierStart(char character)
        {
            return char.IsLetter(character) ||
                character == '_' ||
                CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.LetterNumber;
        }

        private static bool IsIdentifierPart(char character)
        {
            return char.IsDigit(character) ||
                   IsIdentifierStart(character) ||
                   IsIdentifierPartByUnicodeCategory(character);
        }

        private static bool IsIdentifierPartByUnicodeCategory(char character)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(character);

            return category == UnicodeCategory.NonSpacingMark || // Mn
                category == UnicodeCategory.SpacingCombiningMark || // Mc
                category == UnicodeCategory.ConnectorPunctuation || // Pc
                category == UnicodeCategory.Format; // Cf
        }

        public static string SanitizeClassName(string inputName)
        {
            if (inputName == null)
            {
                throw new ArgumentNullException(nameof(inputName));
            }

            if (!IsIdentifierStart(inputName[0]) && IsIdentifierPart(inputName[0]))
            {
                inputName = "_" + inputName;
            }

            var builder = new StringBuilder(inputName.Length);
            for (var i = 0; i < inputName.Length; i++)
            {
                var ch = inputName[i];
                builder.Append(IsIdentifierPart(ch) ? ch : '_');
            }

            return builder.ToString();
        }
    }
}
