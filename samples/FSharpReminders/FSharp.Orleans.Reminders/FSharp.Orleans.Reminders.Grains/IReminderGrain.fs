module FSharp.Orleans.Reminders.Grains.IReminderGrain

open Orleans

type IReminderGrain =
    inherit IGrainWithStringKey
    inherit IRemindable
    
    abstract member WakeUp : unit