# Orleans Dashboard integration for Advanced Reminders

Install `Microsoft.Orleans.Dashboard.AdvancedReminders` on the silos that host the Dashboard grain.
Configure Dashboard and an Advanced Reminders provider on those silos, then call
`AddAdvancedReminderDashboard()` on the silo builder. A separate Dashboard web host only needs
the base Dashboard package to display the data from those silos.
