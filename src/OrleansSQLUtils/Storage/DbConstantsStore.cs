using System.Collections.Generic;

namespace Orleans.SqlUtils
{
    internal static class DbConstantsStore
    {
        private static readonly Dictionary<string, DbConstants> invariantNameToConsts =
            new Dictionary<string, DbConstants>
            {
                {
                    AdoNetInvariants.InvariantNameSqlServer,
                    new DbConstants(startEscapeIndicator: '[', 
                                    endEscapeIndicator: ']',
                                    unionAllSelectTemplate: " UNION ALL SELECT ")
                },
                {AdoNetInvariants.InvariantNameMySql, new DbConstants(
                                    startEscapeIndicator: '`',
                                    endEscapeIndicator: '`',
                                    unionAllSelectTemplate: " UNION ALL SELECT ")
                },
                {AdoNetInvariants.InvariantNameOracleDatabase, new DbConstants(
                                    startEscapeIndicator: '\"',
                                    endEscapeIndicator: '\"',
                                    unionAllSelectTemplate: " UNION ALL SELECT FROM DUAL ")},
            };

        public static DbConstants GetDbConstants(string invariantName)
        {
            return invariantNameToConsts[invariantName];
        }
    }

    internal class DbConstants
    {
        /// <summary>
        /// A query template for union all select
        /// </summary>
        public readonly string UnionAllSelectTemplate;

        /// <summary>
        /// The character that indicates a start escape key for columns and tables that are reserved words.
        /// </summary>
        public readonly char StartEscapeIndicator;

        /// <summary>
        /// The character that indicates an end escape key for columns and tables that are reserved words.
        /// </summary>
        public readonly char EndEscapeIndicator;

        public DbConstants(char startEscapeIndicator, char endEscapeIndicator, string unionAllSelectTemplate)
        {
            StartEscapeIndicator = startEscapeIndicator;
            EndEscapeIndicator = endEscapeIndicator;
            UnionAllSelectTemplate = unionAllSelectTemplate;
        }
    }
}
