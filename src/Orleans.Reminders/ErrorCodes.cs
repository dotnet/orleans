// ReSharper disable InconsistentNaming
namespace Orleans.Reminders;

/// <summary>
/// The set of error codes used by the Orleans runtime libraries for logging errors. For Reminders.
/// </summary>
public enum RSErrorCode
{
    /// <summary>Defines the base value for reminder service log event codes.</summary>
    ReminderServiceBase = /* Runtime */ 100000 + 2900,

    /// <summary>Identifies the <c>RS_Register_TableError</c> log event.</summary>
    RS_Register_TableError = ReminderServiceBase + 5,

    /// <summary>Identifies the <c>RS_Register_AlreadyRegistered</c> log event.</summary>
    RS_Register_AlreadyRegistered = ReminderServiceBase + 7,

    /// <summary>Identifies the <c>RS_Register_InvalidPeriod</c> log event.</summary>
    RS_Register_InvalidPeriod = ReminderServiceBase + 8,

    /// <summary>Identifies the <c>RS_Register_NotRemindable</c> log event.</summary>
    RS_Register_NotRemindable = ReminderServiceBase + 9,

    /// <summary>Identifies the <c>RS_NotResponsible</c> log event.</summary>
    RS_NotResponsible = ReminderServiceBase + 10,

    /// <summary>Identifies the <c>RS_Unregister_NotFoundLocally</c> log event.</summary>
    RS_Unregister_NotFoundLocally = ReminderServiceBase + 11,

    /// <summary>Identifies the <c>RS_Unregister_TableError</c> log event.</summary>
    RS_Unregister_TableError = ReminderServiceBase + 12,

    /// <summary>Identifies the <c>RS_Table_Insert</c> log event.</summary>
    RS_Table_Insert = ReminderServiceBase + 13,

    /// <summary>Identifies the <c>RS_Table_Remove</c> log event.</summary>
    RS_Table_Remove = ReminderServiceBase + 14,

    /// <summary>Identifies the <c>RS_Tick_Delivery_Error</c> log event.</summary>
    RS_Tick_Delivery_Error = ReminderServiceBase + 15,

    /// <summary>Identifies the <c>RS_Not_Started</c> log event.</summary>
    RS_Not_Started = ReminderServiceBase + 16,

    /// <summary>Identifies the <c>RS_UnregisterGrain_TableError</c> log event.</summary>
    RS_UnregisterGrain_TableError = ReminderServiceBase + 17,

    /// <summary>Identifies the <c>RS_GrainBasedTable1</c> log event.</summary>
    RS_GrainBasedTable1 = ReminderServiceBase + 18,

    /// <summary>Identifies the <c>RS_Factory1</c> log event.</summary>
    RS_Factory1 = ReminderServiceBase + 19,

    /// <summary>Identifies the <c>RS_FailedToReadTableAndStartTimer</c> log event.</summary>
    RS_FailedToReadTableAndStartTimer = ReminderServiceBase + 20,

    /// <summary>Identifies the <c>RS_TableGrainInit1</c> log event.</summary>
    RS_TableGrainInit1 = ReminderServiceBase + 21,

    /// <summary>Identifies the <c>RS_TableGrainInit2</c> log event.</summary>
    RS_TableGrainInit2 = ReminderServiceBase + 22,

    /// <summary>Identifies the <c>RS_TableGrainInit3</c> log event.</summary>
    RS_TableGrainInit3 = ReminderServiceBase + 23,

    /// <summary>Identifies the <c>RS_GrainBasedTable2</c> log event.</summary>
    RS_GrainBasedTable2 = ReminderServiceBase + 24,

    /// <summary>Identifies the <c>RS_ServiceStarting</c> log event.</summary>
    RS_ServiceStarting = ReminderServiceBase + 25,

    /// <summary>Identifies the <c>RS_ServiceStarted</c> log event.</summary>
    RS_ServiceStarted = ReminderServiceBase + 26,

    /// <summary>Identifies the <c>RS_ServiceStopping</c> log event.</summary>
    RS_ServiceStopping = ReminderServiceBase + 27,

    /// <summary>Identifies the <c>RS_RegisterOrUpdate</c> log event.</summary>
    RS_RegisterOrUpdate = ReminderServiceBase + 28,

    /// <summary>Identifies the <c>RS_Unregister</c> log event.</summary>
    RS_Unregister = ReminderServiceBase + 29,

    /// <summary>Identifies the <c>RS_Stop</c> log event.</summary>
    RS_Stop = ReminderServiceBase + 30,

    /// <summary>Identifies the <c>RS_RemoveFromTable</c> log event.</summary>
    RS_RemoveFromTable = ReminderServiceBase + 31,

    /// <summary>Identifies the <c>RS_GetReminder</c> log event.</summary>
    RS_GetReminder = ReminderServiceBase + 32,

    /// <summary>Identifies the <c>RS_GetReminders</c> log event.</summary>
    RS_GetReminders = ReminderServiceBase + 33,

    /// <summary>Identifies the <c>RS_RangeChanged</c> log event.</summary>
    RS_RangeChanged = ReminderServiceBase + 34,

    /// <summary>Identifies the <c>RS_LocalStop</c> log event.</summary>
    RS_LocalStop = ReminderServiceBase + 35,

    /// <summary>Identifies the <c>RS_Started</c> log event.</summary>
    RS_Started = ReminderServiceBase + 36,

    /// <summary>Identifies the <c>RS_ServiceInitialLoadFailing</c> log event.</summary>
    RS_ServiceInitialLoadFailing = ReminderServiceBase + 37,

    /// <summary>Identifies the <c>RS_ServiceInitialLoadFailed</c> log event.</summary>
    RS_ServiceInitialLoadFailed = ReminderServiceBase + 38,

    /// <summary>Identifies the <c>RS_FastReminderInterval</c> log event.</summary>
    RS_FastReminderInterval = ReminderServiceBase + 39,
}
