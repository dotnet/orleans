/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

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
