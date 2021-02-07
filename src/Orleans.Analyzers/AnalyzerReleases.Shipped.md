## Release 3.3.0 

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|--------------------
ORLEANS0001  | Usage   | Error  | [AlwaysInterleave] must only be used on the grain interface method and not the grain class method
ORLEANS0002  | Usage   | Error  | Reference parameter modifiers are not allowed
ORLEANS0003  | Usage   | Warning | Non-abstract classes that implement IGrain should derive from the base class Orleans.Grain