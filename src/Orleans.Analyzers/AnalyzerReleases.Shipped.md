## Release 3.3.0

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|--------------------
ORLEANS0003  | Usage   | Error  | Inherit from Grain

## Release 7.0.0

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|--------------------
ORLEANS0001  | Usage   | Error  | [AlwaysInterleave] must only be used on the grain interface method and not the grain class method
ORLEANS0002  | Usage   | Error  | Reference parameter modifiers are not allowed
ORLEANS0004  | Usage   | Error  | Add serialization [Id] and [NonSerialized] attributes
ORLEANS0005  | Usage   | Info   | Add [GenerateSerializer] attribute to [Serializable] type
ORLEANS0006  | Usage   | Error  | Abstract/serialized properties cannot be serialized
ORLEANS0007  | Usage   | Error  | 
ORLEANS0008  | Usage   | Error  | Grain interfaces cannot have properties
ORLEANS0009  | Usage   | Error  | Grain interface methods must return a compatible type
ORLEANS0010  | Usage   | Info   | Add missing [Alias] attribute
ORLEANS0011  | Usage   | Error  | The [Alias] attribute must be unique to the declaring type
ORLEANS0012  | Usage   | Error  | The [Id] attribute must be unique to each members of the declaring type
ORLEANS0013  | Usage   | Error  | This attribute should not be used on grain implementations

### Removed Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|--------------------
ORLEANS0003  | Usage   | Error  | Inherit from Grain
