namespace Orleans.Configuration
{
    /// <summary>
    /// Specifies how ADO.NET grain storage maps a grain state to the key columns of the <c>OrleansStorage</c> table.
    /// </summary>
    /// <remarks>
    /// The key format determines where state is stored. Changing the format of an existing database makes state
    /// written with the previous format unreachable; no automatic migration is performed.
    /// </remarks>
    public enum AdoNetGrainStorageKeyFormat
    {
        /// <summary>
        /// Stores the state name in <c>GrainTypeString</c> and keys rows by the grain key only. Integer and GUID keys
        /// are recognized by parsing the grain key.
        /// </summary>
        /// <remarks>
        /// This is the format used by earlier releases and by Orleans v3 migrations which name each state after the
        /// Orleans v3 grain class. Grains of different types which share a key and state name share one row, and
        /// string keys which parse to the same integer or GUID key, such as <c>"20235"</c> and <c>"020235"</c>,
        /// share one row. Rows are also distinguished only by hash when keys differ in ways the database collation
        /// ignores, such as letter case on default SQL Server and MySQL collations.
        /// </remarks>
        Legacy = 0,

        /// <summary>
        /// Stores the grain type, state name, and grain key as hexadecimal text of their exact bytes, so that each
        /// combination of grain type, state name, and grain key has its own row under any database collation and hasher.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>GrainTypeString</c> holds <c>"{hex(grain type)}/{hex(UTF-8 state name)}"</c> and
        /// <c>GrainIdExtensionString</c> holds <c>hex(grain key)</c>; <c>GrainIdN0</c> and <c>GrainIdN1</c> are zero.
        /// For example, grain <c>"cart/1A"</c> with state <c>"state"</c> is stored with <c>GrainTypeString</c>
        /// <c>"63617274/7374617465"</c> and <c>GrainIdExtensionString</c> <c>"3141"</c>.
        /// </para>
        /// <para>
        /// Hexadecimal text doubles the stored length: grain types and state names together, and grain keys, are limited
        /// to about 255 bytes by the 512-character key columns. Operations on longer identities throw an
        /// <see cref="System.ArgumentException"/>.
        /// This format cannot be combined with <see cref="AdoNetGrainStorageOptions.UseOrleans3CompatibleHasher"/>.
        /// </para>
        /// </remarks>
        GrainTypeQualified = 1,
    }
}
