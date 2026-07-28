---
title: Silo error code monitoring
description: Explore the various silo error code monitoring values in .NET Orleans.
ms.date: 05/23/2025
ms.topic: error-reference
---

# Silo error code monitoring

| Group | Log Type | Log Code Values | Threshold | Description |
|--|--|--|--|--|
| Azure Problems | Warning or Error | 100800 - 100899 | Any Error or Warning | Transient problems reading or writing to Azure table store will be logged as Warning. Transient read errors will automatically be retried. A final Error log message means there is a real problem connecting to Azure table storage. |
| Membership Connectivity Problems | Warning or Error | 100600 - 100699 | Any Error or Warning | Warning logs are an early indication of network connectivity problems and/or silo restart/migration. Ping timeouts and silo-dead votes will show up as Warning messages. Silo detecting it was voted dead will show as an Error message. |
| Grain call timeouts | Warning | 100157 | Multiple Warnings logged in a short amount of time | Grain-call timeout problems are generally caused by temporary network connectivity issues or silo restart/reboot problems. The system should recover after a short time (depending on Liveness config settings) at which point Timeouts should clear. Ideally, monitoring for just the bulk log code 600157 variety of these warnings should be sufficient. |
| Silo Restart / Migration | Warning | 100601 or 100602 | Any Warning | Warning printed when silo detects it was restarted on same machine (100602) or migrated to a different machine (100601) |
| Network Socket Problems | Warning or Error | 101000 to 101999, 100307,100015, 100016 | Any Error or Warning | Socket disconnects are logged as Warning messages. Problems opening sockets or during message transmission, are logged as Errors. |
| Grain problems | Warning or Error | 101534 | Any Error or Warning | Detection of "stuck" requests for non-reentrant grains. The error code is reported every time a request takes longer than 5x request timeout time to execute. |
