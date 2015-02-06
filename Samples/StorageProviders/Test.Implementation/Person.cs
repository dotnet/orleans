//*********************************************************
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
using System.Linq;
using System.Threading.Tasks;
using System.Text;
using Orleans;
using Orleans.Providers;
using Test.Interfaces;

namespace Test.Implementation
{
    [StorageProvider(ProviderName = "TestStore")]
    public class Person : Grain<IPersonState>, Test.Interfaces.IPerson
    {
        Task IPerson.SetPersonalAttributes(PersonalAttributes props)
        {
            State.FirstName = props.FirstName;
            State.LastName = props.LastName;
            State.Gender = props.Gender;
            return State.WriteStateAsync();
        }

        Task<string> IPerson.GetFirstName()
        {
            return Task.FromResult(State.FirstName);
        }

        Task<string> IPerson.GetLastName()
        {
            return Task.FromResult(State.LastName);
        }

        Task<GenderType> IPerson.GetGender()
        {
            return Task.FromResult(State.Gender);
        }
    }

    public interface IPersonState : IGrainState
    {
        string FirstName { get; set; }
        string LastName { get; set; }
        GenderType Gender { get; set; }
    }
}
