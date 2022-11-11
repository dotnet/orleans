module FSharp.Orleans.Reminders.Grains.ReminderGrain

open System
open System.Threading.Tasks
open FSharp.Orleans.Reminders.Grains.IReminderGrain
open Orleans
open Orleans.Runtime

type ReminderGrain() =
    inherit Grain()
    
    interface IReminderGrain with
        member this.ReceiveReminder(reminderName:string, status:TickStatus) : Task =
            task{
                // Put your logic here
                return! Task.CompletedTask
            }
            
        member this.WakeUp = () 
                                    
    override _.OnActivateAsync() = 
        let _periodTimeInSeconds = TimeSpan.FromSeconds 60
        let _dueTimeInSeconds = TimeSpan.FromSeconds 60
        let _reminder = base.RegisterOrUpdateReminder(base.GetPrimaryKeyString(), _dueTimeInSeconds, _periodTimeInSeconds)
        base.OnActivateAsync()