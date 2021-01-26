## Release 3.3.0 

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|--------------------
ORLEANS0100  | Usage   | Error  | Types which have a base type belonging to a reference assembly may not be correctly serialized
ORLEANS0101  | Usage   | Error  | Support for generic grain methods requires the project to reference Microsoft.Orleans.Core
ORLEANS0102  | Usage   | Warning | Grain classes must be accessible from generated code
ORLEANS0103  | Usage   | Error  | Grain method return types must be awaitable types such as Task, Task<T>, ValueTask, ValueTask<T>
