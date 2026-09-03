using System;
using System.Collections.Generic;
using System.Linq;
using Orleans.Runtime;
using Orleans.Transactions.Abstractions;

namespace Orleans.Transactions
{
    /// <summary>
    /// Identifies a transaction participant and the protocol roles which it supports.
    /// </summary>
    [Serializable, GenerateSerializer, Immutable]
    public readonly struct ParticipantId
    {
        /// <summary>
        /// Gets a comparer which identifies participants by <see cref="Name"/> and <see cref="Reference"/>,
        /// independently of their supported roles.
        /// </summary>
        public static readonly IEqualityComparer<ParticipantId> Comparer = new IdComparer();

        /// <summary>
        /// Specifies the roles which a participant can perform in the transaction commit protocol.
        /// </summary>
        [GenerateSerializer]
        [Flags]
        public enum Role
        {
            /// <summary>
            /// The participant stores transactional state which can be read or written.
            /// </summary>
            Resource = 1 << 0,

            /// <summary>
            /// The participant can coordinate transaction commit.
            /// </summary>
            Manager = 1 << 1,

            /// <summary>
            /// The participant is preferred when selecting the transaction manager.
            /// </summary>
            PriorityManager = 1 << 2
        }

        /// <summary>
        /// Gets the name which identifies the participant's transactional resource within its grain.
        /// </summary>
        [Id(0)]
        public string Name { get; }

        /// <summary>
        /// Gets the reference to the grain which hosts the participant.
        /// </summary>
        [Id(1)]
        public GrainReference Reference { get; }

        /// <summary>
        /// Gets the protocol roles which the participant supports.
        /// </summary>
        [Id(2)]
        public Role SupportedRoles { get; }

        /// <summary>
        /// Initializes a new transaction participant identifier.
        /// </summary>
        /// <param name="name">The name which identifies the transactional resource within its grain.</param>
        /// <param name="reference">The reference to the grain which hosts the participant.</param>
        /// <param name="supportedRoles">The protocol roles which the participant supports.</param>
        public ParticipantId(string name, GrainReference reference, Role supportedRoles)
        {
            this.Name = name;
            this.Reference = reference;
            this.SupportedRoles = supportedRoles;
        }

        /// <summary>
        /// Returns a diagnostic representation of this participant.
        /// </summary>
        /// <returns>A string containing the participant name and grain reference.</returns>
        public override string ToString()
        {
            return $"ParticipantId.{Name}.{Reference}";
        }

        /// <summary>
        /// Compares participant identifiers by resource name and grain reference.
        /// </summary>
        [GenerateSerializer, Immutable]
        public sealed class IdComparer : IEqualityComparer<ParticipantId>
        {
            /// <summary>
            /// Determines whether two participant identifiers refer to the same resource.
            /// </summary>
            /// <param name="x">The first participant identifier to compare.</param>
            /// <param name="y">The second participant identifier to compare.</param>
            /// <returns>
            /// <see langword="true"/> when both identifiers have the same resource name and grain reference;
            /// otherwise, <see langword="false"/>.
            /// </returns>
            public bool Equals(ParticipantId x, ParticipantId y)
            {
                return string.CompareOrdinal(x.Name, y.Name) == 0 && Equals(x.Reference, y.Reference);
            }

            /// <summary>
            /// Returns a hash code derived from the participant's resource name and grain reference.
            /// </summary>
            /// <param name="obj">The participant identifier.</param>
            /// <returns>A hash code for <paramref name="obj"/>.</returns>
            public int GetHashCode(ParticipantId obj) => HashCode.Combine(obj.Name, obj.Reference);
        }
    }

    /// <summary>
    /// Provides operations for inspecting and selecting transaction participants by protocol role.
    /// </summary>
    public static class ParticipantRoleExtensions
    {
        /// <summary>
        /// Determines whether a participant supports at least one of the specified roles.
        /// </summary>
        /// <param name="participant">The participant to inspect.</param>
        /// <param name="role">The role or roles to test.</param>
        /// <returns>
        /// <see langword="true"/> when the participant supports at least one specified role;
        /// otherwise, <see langword="false"/>.
        /// </returns>
        public static bool SupportsRoles(this ParticipantId participant, ParticipantId.Role role)
        {
            return (participant.SupportedRoles & role) != 0;
        }

        /// <summary>
        /// Determines whether a participant stores transactional state.
        /// </summary>
        /// <param name="participant">The participant to inspect.</param>
        /// <returns><see langword="true"/> when the participant supports the resource role; otherwise, <see langword="false"/>.</returns>
        public static bool IsResource(this ParticipantId participant)
        {
            return participant.SupportsRoles(ParticipantId.Role.Resource);
        }

        /// <summary>
        /// Determines whether a participant can coordinate transaction commit.
        /// </summary>
        /// <param name="participant">The participant to inspect.</param>
        /// <returns><see langword="true"/> when the participant supports the manager role; otherwise, <see langword="false"/>.</returns>
        public static bool IsManager(this ParticipantId participant)
        {
            return participant.SupportsRoles(ParticipantId.Role.Manager);
        }

        /// <summary>
        /// Determines whether a participant is preferred when selecting the transaction manager.
        /// </summary>
        /// <param name="participant">The participant to inspect.</param>
        /// <returns><see langword="true"/> when the participant supports the priority-manager role; otherwise, <see langword="false"/>.</returns>
        public static bool IsPriorityManager(this ParticipantId participant)
        {
            return participant.SupportsRoles(ParticipantId.Role.PriorityManager);
        }

        /// <summary>
        /// Selects participants which store transactional state.
        /// </summary>
        /// <param name="participants">The participants and their transaction access counts.</param>
        /// <returns>The participants which support the resource role, with their access counts.</returns>
        public static IEnumerable<KeyValuePair<ParticipantId, AccessCounter>> SelectResources(this IEnumerable<KeyValuePair<ParticipantId, AccessCounter>> participants)
        {
            return participants.Where(p => p.Key.IsResource());
        }

        /// <summary>
        /// Selects participants which can coordinate transaction commit.
        /// </summary>
        /// <param name="participants">The participants and their transaction access counts.</param>
        /// <returns>The participants which support the manager role, with their access counts.</returns>
        public static IEnumerable<KeyValuePair<ParticipantId, AccessCounter>> SelectManagers(this IEnumerable<KeyValuePair<ParticipantId, AccessCounter>> participants)
        {
            return participants.Where(p => p.Key.IsManager());
        }

        /// <summary>
        /// Selects participants which are preferred when choosing the transaction manager.
        /// </summary>
        /// <param name="participants">The participants to filter.</param>
        /// <returns>The participants which support the priority-manager role.</returns>
        public static IEnumerable<ParticipantId> SelectPriorityManagers(this IEnumerable<ParticipantId> participants)
        {
            return participants.Where(p => p.IsPriorityManager());
        }
    }
}
