---
title: Client error code monitoring
description: Explore the various client error code monitoring values in .NET Orleans.
ms.date: 05/23/2025
ms.topic: error-reference
---

# Client error code monitoring

| Group | Log type | Log code values | Threshold | Description |
|--|--|--|--|--|
| Azure Problems | Warning or Error | 100800 - 100899 | Any Error or Warning | Transient problems reading or writing to Azure table store will be logged as Warning. Transient read errors will automatically be retried. A final Error log message means there is a real problem connecting to Azure table storage. |
| Gateway connectivity problems | Warning or Error | 100901 - 100904, 100912, 100913, 100921, 100923, 100158, 100161, 100178, , 101313 | Any Error or Warning | Problems connecting to gateways. No active gateways in the Azure table. Connection to active gateway lost. |
| Grain call timeouts | Warning | 100157 | Multiple Warnings logged in a short amount of time | Grain-call timeout problems are generally caused by temporary network connectivity issues or silo restart/reboot problems. The system should recover after a short time (depending on Liveness config settings) at which point Timeouts should clear. Ideally, monitoring for just the bulk log code 600157 variety of these warnings should be sufficient. |
| Network Socket Problems | Warning or Error | 101000 to 101999, 100307, 100015, 100016 | Any Error or Warning | Socket disconnects are logged as Warning messages. Problems opening sockets or during message transmission, are logged as Errors. |
| Bulk log message compaction | Any | 500000 or higher | Message summary based on bulk message threshold settings | If multiple logs of the same log code occur within a designated time interval (the default is >5 within 1 minute) then additional log messages with that log code are suppressed and output as a "bulk" entry with log code equal to the original log code + 500000. So for example, multiple 100157 entries will show in the logs as 5 x 100157 + 1 x 600157 log entries per minute. |
