using System;
using Orleans.SqlUtils.StorageProvider.GrainInterfaces;

namespace Orleans.SqlUtils.StorageProvider.GrainClasses
{
    [Serializable]
    [GenerateSerializer]
    public class DeviceState
    {
        [Id(0)]
        public ICustomerGrain Owner { get; set; }
        [Id(1)]
        public string SerialNumber { get; set; }
        [Id(2)]
        public long EventId { get; set; }
        [Id(3)]
        public int VehicleId { get; set; }
        [Id(4)]
        public short CustomerId { get; set; }
        [Id(5)]
        public short CompanyId { get; set; }
        [Id(6)]
        public short SoftwareId { get; set; }
        [Id(7)]
        public short StatusId { get; set; }
        [Id(8)]
        public short LifeCycleId { get; set; }
        [Id(9)]
        public int DateKey { get; set; }
        [Id(10)]
        public int TimeKey { get; set; }
        [Id(11)]
        public short MillisecondKey { get; set; }
        [Id(12)]
        public int FaultId { get; set; }
        [Id(13)]
        public short SystemId { get; set; }
        [Id(14)]
        public short EventTypeId { get; set; }
        [Id(15)]
        public int LocationId { get; set; }
        [Id(16)]
        public double Latitude { get; set; }
        [Id(17)]
        public double Longitude { get; set; }
        [Id(18)]
        public DateTime TriggerTime { get; set; }
        [Id(19)]
        public long Altitude { get; set; }
        [Id(20)]
        public long Heading { get; set; }
        [Id(21)]
        public int PeakBusUtilization { get; set; }
        [Id(22)]
        public int TripId { get; set; }
        [Id(23)]
        public int CurrentBusUtilization { get; set; }
        [Id(24)]
        public int TotalSnapshots { get; set; }
        [Id(25)]
        public bool ProtectLampOn { get; set; }
        [Id(26)]
        public bool AmberWarningLampOn { get; set; }
        [Id(27)]
        public bool RedStopLampOn { get; set; }
        [Id(28)]
        public bool MalfunctionIndicatorLampOn { get; set; }
        [Id(29)]
        public bool FlashProtectLampOn { get; set; }
        [Id(30)]
        public bool FlashAmberWarningLampOn { get; set; }
        [Id(31)]
        public bool FlashRedStopLampOn { get; set; }
        [Id(32)]
        public bool FlashMalfunctionIndicatorLampOn { get; set; }
        [Id(33)]
        public int ConversionMethod { get; set; }
        [Id(34)]
        public int OccurrenceCount { get; set; }
        [Id(35)]
        public int PreTriggerSamples { get; set; }
        [Id(36)]
        public int PostTriggerSamples { get; set; }
        [Id(37)]
        public double AllLampsOnTime { get; set; }
        [Id(38)]
        public int AmberLampCount { get; set; }
        [Id(39)]
        public double AmberLampTime { get; set; }
        [Id(40)]
        public int RedLampCount { get; set; }
        [Id(41)]
        public double RedLampTime { get; set; }
        [Id(42)]
        public int MilLampCount { get; set; }
        [Id(43)]
        public double MilLampTime { get; set; }
        [Id(44)]
        public double EngineStartAmbient { get; set; }
        [Id(45)]
        public double EngineStartCoolant { get; set; }
        [Id(46)]
        public double TotalDistance { get; set; }
        [Id(47)]
        public double TotalEngineHours { get; set; }
        [Id(48)]
        public double TotalIdleFuel { get; set; }
        [Id(49)]
        public double TotalIdleHours { get; set; }
        [Id(50)]
        public double TotalFuel { get; set; }
        [Id(51)]
        public double TotalPtoFuel { get; set; }
        [Id(52)]
        public Guid TransactionId { get; set; }
        [Id(53)]
        public string MessageId { get; set; }
        [Id(54)]
        public short LampId { get; set; }
        [Id(55)]
        public short EngineFamilyId { get; set; }
    }
}