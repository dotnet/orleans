﻿//*********************************************************//
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

namespace TwitterGrainInterfaces
{
    /// <summary>
    /// A DTO to return sentiment score for a hashtag 
    /// </summary>
    public class Totals
    {
        public int Positive { get; set; }
        public int Negative { get; set; }
        public int Total { get; set; }
        public string Hashtag { get; set; }
        public DateTime LastUpdated { get; set; }
        public string LastTweet { get; set; }

    }
}
