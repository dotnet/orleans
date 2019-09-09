using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;

namespace OneBoxDeployment.Api.Dtos
{
    /// <summary>
    /// A test DTO class for OneBoxDeployment increment test.
    /// </summary>
    [DebuggerDisplay("Increment(GrainId = {GrainId}, IncrementBy = {IncrementBy}")]
    public sealed class Increment: IEquatable<Increment>
    {
        /// <summary>
        /// The identifier for the grain.
        /// </summary>
        [Required]
        [ReadOnly(false)]
        public int GrainId { get; set; }

        /// <summary>
        /// The amount to increment the state.
        /// </summary>
        [Required]
        [ReadOnly(false)]
        public int IncrementBy { get; set; }

        /// <inheritdoc />
        /// <remarks>Beware resolution of the hash when using the tuple technique.</remarks>
        public override int GetHashCode() => (GrainId, IncrementBy).GetHashCode();

        /// <inheritdoc />
        public override bool Equals(object obj) => obj is Increment left && Equals(left);

        /// <inheritdoc />
        public bool Equals(Increment other) => GrainId == other.GrainId && IncrementBy == other.IncrementBy;
    }
}
