// ReSharper disable InconsistentNaming
namespace Orleans.Reminders;

/// <summary>
/// The set of error codes used by the Orleans runtime libraries for logging errors. For Reminders.
/// </summary>
public enum RSErrorCode
{
    ReminderServiceBase = /* Runtime */ 100000 + 2900,
    RS_Register_TableError = ReminderServiceBase + 5,
    RS_Register_AlreadyRegistered = ReminderServiceBase + 7,
    RS_Register_InvalidPeriod = ReminderServiceBase + 8,
    RS_Register_NotRemindable = ReminderServiceBase + 9,
    RS_NotResponsible = ReminderServiceBase + 10,
    RS_Unregister_NotFoundLocally = ReminderServiceBase + 11,
    RS_Unregister_TableError = ReminderServiceBase + 12,
    RS_Table_Insert = ReminderServiceBase + 13,
    RS_Table_Remove = ReminderServiceBase + 14,
    RS_Tick_Delivery_Error = ReminderServiceBase + 15,
    RS_Not_Started = ReminderServiceBase + 16,
    RS_UnregisterGrain_TableError = ReminderServiceBase + 17,
    RS_GrainBasedTable1 = ReminderServiceBase + 18,
    RS_Factory1 = ReminderServiceBase + 19,
    RS_FailedToReadTableAndStartTimer = ReminderServiceBase + 20,
    RS_TableGrainInit1 = ReminderServiceBase + 21,
    RS_TableGrainInit2 = ReminderServiceBase + 22,
    RS_TableGrainInit3 = ReminderServiceBase + 23,
    RS_GrainBasedTable2 = ReminderServiceBase + 24,
    RS_ServiceStarting = ReminderServiceBase + 25,
    RS_ServiceStarted = ReminderServiceBase + 26,
    RS_ServiceStopping = ReminderServiceBase + 27,
    RS_RegisterOrUpdate = ReminderServiceBase + 28,
    RS_Unregister = ReminderServiceBase + 29,
    RS_Stop = ReminderServiceBase + 30,
    RS_RemoveFromTable = ReminderServiceBase + 31,
    RS_GetReminder = ReminderServiceBase + 32,
    RS_GetReminders = ReminderServiceBase + 33,
    RS_RangeChanged = ReminderServiceBase + 34,
    RS_LocalStop = ReminderServiceBase + 35,
    RS_Started = ReminderServiceBase + 36,
    RS_ServiceInitialLoadFailing = ReminderServiceBase + 37,
    RS_ServiceInitialLoadFailed = ReminderServiceBase + 38,
    RS_FastReminderInterval = ReminderServiceBase + 39,
}
