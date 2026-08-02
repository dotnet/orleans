namespace Orleans;

/// <summary>
/// This enum provides options for controlling the field id generation logic.
/// </summary>
public enum GenerateFieldIds
{
    /// <summary>
    /// Only members explicitly annotated with a field id will be serialized. This is the default.
    /// </summary>
    None,
    /// <summary>
    /// Field ids will be automatically assigned to eligible public properties. To qualify, a property must have an accessible getter, and either an accessible setter or a corresponding constructor parameter.
    /// </summary>
    /// <remarks>
    /// The presence of an explicit field id annotation on any member of a type will automatically disable automatic field id generation for that type.
    /// </remarks>
    PublicProperties
}
