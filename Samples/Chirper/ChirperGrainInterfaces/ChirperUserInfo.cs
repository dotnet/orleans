using System;
using Orleans;

namespace Orleans.Samples.Chirper.GrainInterfaces
{
    /// <summary>
    /// Data object representing key metadata for one Chirper user
    /// </summary>
    [Serializable]
    public struct ChirperUserInfo : IEquatable<ChirperUserInfo>
    {
        /// <summary>Unique Id for this user</summary>
        public long UserId { get; private set; }

        /// <summary>Alias / username for this user</summary>
        public string UserAlias { get; private set; }

        public static ChirperUserInfo GetUserInfo(long userId, string userAlias)
        {
            return new ChirperUserInfo { UserId = userId, UserAlias = userAlias };
        }

        public override string ToString()
        {
            return "ChirperUser:Alias=" + UserAlias + ",Id=" + UserId;
        }

        public bool Equals(ChirperUserInfo other)
        {
            return this.UserId == other.UserId;
        }

        public override bool Equals(object obj)
        {
            return base.Equals(obj);
        }

        public override int GetHashCode()
        {
            return UserId.GetHashCode();
        }
    }
}
