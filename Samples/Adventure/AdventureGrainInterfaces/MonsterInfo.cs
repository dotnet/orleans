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
using System.Collections.Generic;

using Orleans;
using Orleans.Concurrency;

namespace AdventureGrainInterfaces
{
    [Immutable]
    public class MonsterInfo
    {
        public long Id { get; set; }
        public string Name { get; set; }
        public List<long> KilledBy { get; set; }
    }
}
