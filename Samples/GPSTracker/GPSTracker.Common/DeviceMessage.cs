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
namespace GPSTracker.Common
{
    public class DeviceMessage
    {

        public DeviceMessage()
        { }

        public DeviceMessage(double latitude, double longitude, int messageId, int deviceId, DateTime timestamp)
        {
            this.Latitude = latitude;
            this.Longitude = longitude;
            this.MessageId = messageId;
            this.DeviceId = deviceId;
            this.Timestamp = timestamp;
        }

        public int DeviceId { get; set; }
        public int MessageId { get; set; }
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class MessageBatch
    {
        public DeviceMessage[] Messages;
    }
}
