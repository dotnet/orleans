using System;
using Orleans;
using Orleans.SqlUtils.StorageProvider.GrainInterfaces;

namespace Orleans.SqlUtils.StorageProvider.GrainClasses
{
    [Serializable]
    public class DeviceState
    {
        public ICustomerGrain Owner { get; set; }
        public string SerialNumber { get; set; }
        public long EventId { get; set; }
        public int VehicleId { get; set; }
        public short CustomerId { get; set; }
        public short CompanyId { get; set; }
        public short SoftwareId { get; set; }
        public short StatusId { get; set; }
        public short LifeCycleId { get; set; }
        public int DateKey { get; set; }
        public int TimeKey { get; set; }
        public short MillisecondKey { get; set; }
        public int FaultId { get; set; }
        public short SystemId { get; set; }
        public short EventTypeId { get; set; }
        public int LocationId { get; set; }
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public DateTime TriggerTime { get; set; }
        public long Altitude { get; set; }
        public long Heading { get; set; }
        public int PeakBusUtilization { get; set; }
        public int TripId { get; set; }
        public int CurrentBusUtilization { get; set; }
        public int TotalSnapshots { get; set; }
        public bool ProtectLampOn { get; set; }
        public bool AmberWarningLampOn { get; set; }
        public bool RedStopLampOn { get; set; }
        public bool MalfunctionIndicatorLampOn { get; set; }
        public bool FlashProtectLampOn { get; set; }
        public bool FlashAmberWarningLampOn { get; set; }
        public bool FlashRedStopLampOn { get; set; }
        public bool FlashMalfunctionIndicatorLampOn { get; set; }
        public int ConversionMethod { get; set; }
        public int OccurrenceCount { get; set; }
        public int PreTriggerSamples { get; set; }
        public int PostTriggerSamples { get; set; }
        public double AllLampsOnTime { get; set; }
        public int AmberLampCount { get; set; }
        public double AmberLampTime { get; set; }
        public int RedLampCount { get; set; }
        public double RedLampTime { get; set; }
        public int MilLampCount { get; set; }
        public double MilLampTime { get; set; }
        public double EngineStartAmbient { get; set; }
        public double EngineStartCoolant { get; set; }
        public double TotalDistance { get; set; }
        public double TotalEngineHours { get; set; }
        public double TotalIdleFuel { get; set; }
        public double TotalIdleHours { get; set; }
        public double TotalFuel { get; set; }
        public double TotalPtoFuel { get; set; }
        public Guid TransactionId { get; set; }
        public string MessageId { get; set; }
        public short LampId { get; set; }
        public short EngineFamilyId { get; set; }
    }
}