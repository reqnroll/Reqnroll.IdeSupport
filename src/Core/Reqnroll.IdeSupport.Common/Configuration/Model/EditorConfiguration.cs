using System;

namespace Reqnroll.IdeSupport.Common.Configuration;

/// <summary>EditorConfiguration</summary>
public class EditorConfiguration
{
    /// <summary>Gets or sets the show step completion after step keywords.</summary>
    public bool ShowStepCompletionAfterStepKeywords { get; set; } = true;
    /// <summary>Gets or sets the gherkin format.</summary>
    public GherkinFormatConfiguration GherkinFormat { get; set; } = new();

    private void FixEmptyContainers()
    {
        GherkinFormat ??= new GherkinFormatConfiguration();
    }

    /// <summary>Validates and normalizes this configuration and its nested Gherkin format configuration.</summary>
    public void CheckConfiguration()
    {
        FixEmptyContainers();

        GherkinFormat.CheckConfiguration();
    }

    #region Equality

    /// <summary>Determines whether this instance has the same setting values as <paramref name="other"/>.</summary>
    protected bool Equals(EditorConfiguration other) =>
        ShowStepCompletionAfterStepKeywords == other.ShowStepCompletionAfterStepKeywords &&
        Equals(GherkinFormat, other.GherkinFormat);

    /// <summary>Determines whether <paramref name="obj"/> is an <see cref="EditorConfiguration"/> with the same setting values.</summary>
    public override bool Equals(object obj)
    {
        if (ReferenceEquals(null, obj)) return false;
        if (ReferenceEquals(this, obj)) return true;
        if (obj.GetType() != GetType()) return false;
        return Equals((EditorConfiguration) obj);
    }

    // ReSharper disable NonReadonlyMemberInGetHashCode
    /// <summary>Returns a hash code derived from the configuration's setting values.</summary>
    public override int GetHashCode()
    {
        unchecked
        {
            return (ShowStepCompletionAfterStepKeywords.GetHashCode() * 397) ^
                   (GherkinFormat != null ? GherkinFormat.GetHashCode() : 0);
        }
    }
    // ReSharper restore NonReadonlyMemberInGetHashCode

    #endregion
}
