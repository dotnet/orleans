namespace Orleans.AdvancedReminders;

[Serializable, GenerateSerializer, Immutable]
internal sealed class ReminderData : IGrainReminder
{
    [Id(0)]
    public Runtime.MissedReminderAction Action { get; }
    [Id(1)]
    public string? CronExpression { get; }
    [Id(2)]
    public string? CronTimeZone { get; }
    [Id(3)]
    public readonly string ETag;
    [Id(4)]
    public readonly GrainId GrainId;
    [Id(5)]
    public readonly string RegistrationId;
    [Id(6)]
    public string ReminderName { get; }

    internal ReminderData(
        GrainId grainId,
        string reminderName,
        string eTag,
        string scheduleId,
        string? cronExpression = null,
        Runtime.MissedReminderAction action = Runtime.MissedReminderAction.Skip,
        string? cronTimeZoneId = null)
    {
        GrainId = grainId;
        ReminderName = reminderName;
        ETag = eTag;
        RegistrationId = Runtime.ReminderService.ReminderScheduleId.GetRegistrationId(scheduleId);
        CronExpression = string.IsNullOrWhiteSpace(cronExpression) ? null : cronExpression;
        Action = action;
        CronTimeZone = string.IsNullOrWhiteSpace(cronTimeZoneId) ? null : cronTimeZoneId;
    }

    public override string ToString() => $"<IOrleansReminder: GrainId={GrainId} ReminderName={ReminderName} RegistrationId={RegistrationId} ETag={ETag} CronExpression={CronExpression} CronTimeZone={CronTimeZone} Action={Action}>";
}
