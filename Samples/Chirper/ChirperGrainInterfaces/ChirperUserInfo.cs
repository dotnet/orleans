//*********************************************************//
//    Copyright (c) Microsoft. All rights reserved.
//    
//    Apache 2.0 License
//    
//    You may obtain a copy of the License at
//    http://www.apache.org/licenses/LICENSE-2.0
//    
//    Unless required by applicable law or agreed to in writing, software 
//    distributed under the License is distributed on an "AS IS" BASIS, 
//    WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or 
//    implied. See the License for the specific language governing 
//    permissions and limitations under the License.
//
//*********************************************************

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
