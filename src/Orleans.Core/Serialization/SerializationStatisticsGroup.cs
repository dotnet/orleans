using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;

namespace Orleans.Serialization
{
    /// <summary>
    /// Statistics related to serialization.
    /// </summary>
    public class SerializationStatisticsGroup
    {
        internal readonly CounterStatistic Copies;
        internal readonly CounterStatistic Serializations;
        internal readonly CounterStatistic Deserializations;
        internal readonly CounterStatistic HeaderSers;
        internal readonly CounterStatistic HeaderDesers;
        internal readonly CounterStatistic HeaderSersNumHeaders;
        internal readonly CounterStatistic HeaderDesersNumHeaders;
        internal readonly CounterStatistic CopyTimeStatistic;
        internal readonly CounterStatistic SerTimeStatistic;
        internal readonly CounterStatistic DeserTimeStatistic;
        internal readonly CounterStatistic HeaderSerTime;
        internal readonly CounterStatistic HeaderDeserTime;
        internal readonly IntValueStatistic TotalTimeInSerializer;

        internal readonly CounterStatistic FallbackSerializations;
        internal readonly CounterStatistic FallbackDeserializations;
        internal readonly CounterStatistic FallbackCopies;
        internal readonly CounterStatistic FallbackSerTimeStatistic;
        internal readonly CounterStatistic FallbackDeserTimeStatistic;
        internal readonly CounterStatistic FallbackCopiesTimeStatistic;

        public SerializationStatisticsGroup(IOptions<StatisticsOptions> statisticsOptions)
        {
            this.CollectSerializationStats = statisticsOptions.Value.CollectionLevel >= StatisticsLevel.Verbose;
            if (this.CollectSerializationStats)
            {
                const CounterStorage store = CounterStorage.LogOnly;
                Copies = CounterStatistic.FindOrCreate(StatisticNames.SERIALIZATION_BODY_DEEPCOPIES, store);
                Serializations = CounterStatistic.FindOrCreate(StatisticNames.SERIALIZATION_BODY_SERIALIZATION, store);
                Deserializations = CounterStatistic.FindOrCreate(StatisticNames.SERIALIZATION_BODY_DESERIALIZATION, store);
                HeaderSers = CounterStatistic.FindOrCreate(StatisticNames.SERIALIZATION_HEADER_SERIALIZATION, store);
                HeaderDesers = CounterStatistic.FindOrCreate(StatisticNames.SERIALIZATION_HEADER_DESERIALIZATION, store);
                HeaderSersNumHeaders = CounterStatistic.FindOrCreate(StatisticNames.SERIALIZATION_HEADER_SERIALIZATION_NUMHEADERS, store);
                HeaderDesersNumHeaders = CounterStatistic.FindOrCreate(StatisticNames.SERIALIZATION_HEADER_DESERIALIZATION_NUMHEADERS, store);
                CopyTimeStatistic = CounterStatistic.FindOrCreate(StatisticNames.SERIALIZATION_BODY_DEEPCOPY_MILLIS, store).AddValueConverter(Utils.TicksToMilliSeconds);
                SerTimeStatistic = CounterStatistic.FindOrCreate(StatisticNames.SERIALIZATION_BODY_SERIALIZATION_MILLIS, store).AddValueConverter(Utils.TicksToMilliSeconds);
                DeserTimeStatistic = CounterStatistic.FindOrCreate(StatisticNames.SERIALIZATION_BODY_DESERIALIZATION_MILLIS, store).AddValueConverter(Utils.TicksToMilliSeconds);
                HeaderSerTime = CounterStatistic.FindOrCreate(StatisticNames.SERIALIZATION_HEADER_SERIALIZATION_MILLIS, store).AddValueConverter(Utils.TicksToMilliSeconds);
                HeaderDeserTime = CounterStatistic.FindOrCreate(StatisticNames.SERIALIZATION_HEADER_DESERIALIZATION_MILLIS, store).AddValueConverter(Utils.TicksToMilliSeconds);

                TotalTimeInSerializer = IntValueStatistic.FindOrCreate(
                    StatisticNames.SERIALIZATION_TOTAL_TIME_IN_SERIALIZER_MILLIS,
                    () =>
                    {
                        long ticks = CopyTimeStatistic.GetCurrentValue() +
                                     SerTimeStatistic.GetCurrentValue() +
                                     DeserTimeStatistic.GetCurrentValue() +
                                     HeaderSerTime.GetCurrentValue() +
                                     HeaderDeserTime.GetCurrentValue();
                        return Utils.TicksToMilliSeconds(ticks);
                    },
                    CounterStorage.LogAndTable);

                const CounterStorage storeFallback = CounterStorage.LogOnly;
                FallbackSerializations = CounterStatistic.FindOrCreate(StatisticNames.SERIALIZATION_BODY_FALLBACK_SERIALIZATION, storeFallback);
                FallbackDeserializations = CounterStatistic.FindOrCreate(StatisticNames.SERIALIZATION_BODY_FALLBACK_DESERIALIZATION, storeFallback);
                FallbackCopies = CounterStatistic.FindOrCreate(StatisticNames.SERIALIZATION_BODY_FALLBACK_DEEPCOPIES, storeFallback);
                FallbackSerTimeStatistic = CounterStatistic.FindOrCreate(StatisticNames.SERIALIZATION_BODY_FALLBACK_SERIALIZATION_MILLIS, storeFallback)
                    .AddValueConverter(Utils.TicksToMilliSeconds);
                FallbackDeserTimeStatistic = CounterStatistic.FindOrCreate(StatisticNames.SERIALIZATION_BODY_FALLBACK_DESERIALIZATION_MILLIS, storeFallback)
                    .AddValueConverter(Utils.TicksToMilliSeconds);
                FallbackCopiesTimeStatistic = CounterStatistic.FindOrCreate(StatisticNames.SERIALIZATION_BODY_FALLBACK_DEEPCOPY_MILLIS, storeFallback)
                    .AddValueConverter(Utils.TicksToMilliSeconds);
            }
        }

        /// <summary>
        /// Gets a value indicating whether or not to collect serialization statistics.
        /// </summary>
        /// <remarks>
        /// Enable serialization statistics collection by setting <see cref="StatisticsOptions.CollectionLevel"/> greater than or equal to <see cref="StatisticsLevel.Verbose"/>.
        /// </remarks>
        public bool CollectSerializationStats { get; }
    }
}