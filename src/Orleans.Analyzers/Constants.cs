namespace Orleans.Analyzers
{
    internal static class Constants
    {
        public const string SystemNamespace = "System";

        public const string GrainBaseFullyQualifiedName = "Orleans.Grain";
        public const string IAddressibleFullyQualifiedName = "Orleans.Runtime.IAddressable";
        public const string IGrainBaseFullyQualifiedName = "Orleans.IGrainBase";
        public const string IGrainFullyQualifiedName = "Orleans.IGrain";
        public const string ISystemTargetFullyQualifiedName = "Orleans.ISystemTarget";

        public const string AliasAttributeFullyQualifiedName = "Orleans.AliasAttribute";
        public const string IdAttributeFullyQualifiedName = "Orleans.IdAttribute";
        public const string GenerateSerializerAttributeFullyQualifiedName = "Orleans.GenerateSerializerAttribute";

        public const string SerializableAttributeFullyQualifiedName = "System.SerializableAttribute";
        public const string NonSerializedAttributeFullyQualifiedName = "System.NonSerializedAttribute";

        public const string AliasAttributeSourceName = "global::Orleans.AliasAttribute";
        public const string IdAttributeSourceName = "global::Orleans.IdAttribute";
        public const string GenerateSerializerAttributeSourceName = "global::Orleans.GenerateSerializerAttribute";
        public const string NonSerializedAttributeSourceName = "global::System.NonSerializedAttribute";
    }
}
