using System;

namespace Orleans.CodeGenerator.Model.Incremental
{
    internal readonly struct SourceLocationModel : IEquatable<SourceLocationModel>
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

        public bool Equals(SourceLocationModel other)
            => SourceOrderGroup == other.SourceOrderGroup
                && string.Equals(FilePath, other.FilePath, StringComparison.Ordinal)
                && Position == other.Position;

        public override bool Equals(object obj) => obj is SourceLocationModel other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = SourceOrderGroup;
                hash = hash * 31 + StringComparer.Ordinal.GetHashCode(FilePath ?? string.Empty);
                hash = hash * 31 + Position;
                return hash;
            }
        }

        public static bool operator ==(SourceLocationModel left, SourceLocationModel right) => left.Equals(right);
        public static bool operator !=(SourceLocationModel left, SourceLocationModel right) => !left.Equals(right);
    }
}
