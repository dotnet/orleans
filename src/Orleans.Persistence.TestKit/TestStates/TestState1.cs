namespace Orleans.Persistence.TestKit;

/// <summary>
/// A test state used to verify storage provider functionality.
/// </summary>
[Serializable]
[GenerateSerializer]
public class TestState1 : IEquatable<TestState1>
{
    /// <summary>
    /// Gets or sets a string property.
    /// </summary>
    [Id(0)]
    public string A { get; set; }

    /// <summary>
    /// Gets or sets an integer property.
    /// </summary>
    [Id(1)]
    public int B { get; set; }

    /// <summary>
    /// Gets or sets a long property.
    /// </summary>
    [Id(2)]
    public long C { get; set; }

    /// <inheritdoc/>
    public override bool Equals(object obj)
    {
        return Equals(obj as TestState1);
    }

    /// <inheritdoc/>
    public bool Equals(TestState1 other)
    {
        if (other is null)
        {
            return false;
        }

        return EqualityComparer<string>.Default.Equals(A, other.A) && B.Equals(other.B) && C.Equals(other.C);
    }

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        unchecked
        {
            int hash = 17;
            hash = hash * 23 + (A is not null ? EqualityComparer<string>.Default.GetHashCode(A) : 0);
            hash = hash * 23 + B.GetHashCode();
            hash = hash * 23 + C.GetHashCode();

            return hash;
        }
    }
}
