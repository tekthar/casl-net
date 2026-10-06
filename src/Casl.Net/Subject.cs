namespace Casl;

/// <summary>An object tagged with a subject type, for objects whose class doesn't say what they are (CASL's <c>subject()</c>).</summary>
public sealed class SubjectInstance
{
    internal SubjectInstance(string type, object value)
    {
        Type = type;
        Value = value;
    }

    public string Type { get; }

    public object Value { get; }
}

public static class Subject
{
    /// <summary>
    /// Tags <paramref name="value"/> as subject type <paramref name="type"/>, e.g. <c>Subject.Of("Article", dto)</c>.
    /// Tagging an already tagged object with a different type throws; null stays null.
    /// </summary>
    public static SubjectInstance? Of(string type, object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case SubjectInstance tagged when tagged.Type == type:
                return tagged;
            case SubjectInstance tagged:
                throw new InvalidOperationException($"Trying to cast object to subject type {type} but previously it was casted to {tagged.Type}");
            default:
                return new SubjectInstance(type, value);
        }
    }
}
