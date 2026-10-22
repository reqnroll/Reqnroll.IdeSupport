namespace Reqnroll.IdeSupport.Common.Configuration;

/// <summary>BindingDiscoveryConfiguration</summary>
public class BindingDiscoveryConfiguration
{
    /// <summary>Gets or sets the connector path.</summary>
    public string? ConnectorPath { get; set; } = null;

    private void FixEmptyContainers()
    {
    }

    /// <summary>Validates and normalizes this configuration, filling in any empty collections.</summary>
    public void CheckConfiguration()
    {
        FixEmptyContainers();
    }

    #region Equality

    /// <summary>Determines whether this instance has the same connector path as <paramref name="other"/>.</summary>
    protected bool Equals(BindingDiscoveryConfiguration other)
    {
        return ConnectorPath == other.ConnectorPath;
    }

    /// <summary>Determines whether <paramref name="obj"/> is a <see cref="BindingDiscoveryConfiguration"/> with the same connector path.</summary>
    public override bool Equals(object? obj)
    {
        if (obj is null) return false;
        if (ReferenceEquals(this, obj)) return true;
        if (obj.GetType() != GetType()) return false;
        return Equals((BindingDiscoveryConfiguration)obj);
    }

    // ReSharper disable NonReadonlyMemberInGetHashCode
    /// <summary>Returns a hash code derived from the connector path.</summary>
    public override int GetHashCode()
    {
        return (ConnectorPath != null ? ConnectorPath.GetHashCode() : 0);
    }
    // ReSharper restore NonReadonlyMemberInGetHashCode

    #endregion
}