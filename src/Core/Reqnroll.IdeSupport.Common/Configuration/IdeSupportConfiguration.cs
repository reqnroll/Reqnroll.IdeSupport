#nullable disable

using System;

namespace Reqnroll.IdeSupport.Common.Configuration;

// TODO: mention of SpecFlow has been commented out in preparation for full removal.

/// <summary>IdeSupportConfiguration</summary>
public class IdeSupportConfiguration
{
    /// <summary>Gets or sets the configuration change time.</summary>
    public DateTimeOffset ConfigurationChangeTime { get; set; } = DateTimeOffset.MinValue;

    /// <summary>Gets or sets the configuration base folder.</summary>
    public string ConfigurationBaseFolder { get; set; }

    /// <summary>Gets or sets the reqnroll.</summary>
    public ReqnrollConfiguration Reqnroll { get; set; } = new();
    //public SpecFlowConfiguration SpecFlow { get; set; } = new();
    /// <summary>Gets or sets the traceability.</summary>
    public TraceabilityConfiguration Traceability { get; set; } = new();
    /// <summary>Gets or sets the editor.</summary>
    public EditorConfiguration Editor { get; set; } = new();
    /// <summary>Gets or sets the binding discovery.</summary>
    public BindingDiscoveryConfiguration BindingDiscovery { get; set; } = new();

    // old settings to be reviewed
    /// <summary>Gets or sets the processor architecture.</summary>
    public ProcessorArchitectureSetting ProcessorArchitecture { get; set; } = ProcessorArchitectureSetting.AutoDetect;
    /// <summary>Gets or sets the debug connector.</summary>
    public bool DebugConnector { get; set; }
    /// <summary>Gets or sets the default feature language.</summary>
    public string DefaultFeatureLanguage { get; set; } = "en-US";
    /// <summary>Gets or sets the configured binding culture.</summary>
    public string ConfiguredBindingCulture { get; set; } = null;
    /// <summary>Gets the effective binding culture: the configured culture if set, otherwise the default feature language.</summary>
    public string BindingCulture => ConfiguredBindingCulture ?? DefaultFeatureLanguage;
    /// <summary>Gets or sets the snippet expression style.</summary>
    public SnippetExpressionStyle SnippetExpressionStyle { get; set; } = SnippetExpressionStyle.CucumberExpression;


    private void FixEmptyContainers()
    {
        Reqnroll ??= new ReqnrollConfiguration();
        //SpecFlow ??= new SpecFlowConfiguration();
        Traceability ??= new TraceabilityConfiguration();
        Editor ??= new EditorConfiguration();
        BindingDiscovery ??= new BindingDiscoveryConfiguration();
    }

    /// <summary>Validates and normalizes this configuration and all of its nested configuration sections.</summary>
    public void CheckConfiguration()
    {
        FixEmptyContainers();

        Reqnroll.CheckConfiguration();
        //SpecFlow.CheckConfiguration();
        Traceability.CheckConfiguration();
        Editor.CheckConfiguration();
        BindingDiscovery.CheckConfiguration();
    }

    #region Equality

    /// <summary>Determines whether this instance has the same setting values as <paramref name="other"/>.</summary>
    protected bool Equals(IdeSupportConfiguration other) =>
        string.Equals(ConfigurationBaseFolder, other.ConfigurationBaseFolder) && 
        Equals(Reqnroll, other.Reqnroll) &&
        //Equals(SpecFlow, other.SpecFlow) &&
        Equals(Traceability, other.Traceability) && 
        Equals(Editor, other.Editor) &&
        Equals(BindingDiscovery, other.BindingDiscovery) &&
        ProcessorArchitecture == other.ProcessorArchitecture && DebugConnector == other.DebugConnector &&
        string.Equals(DefaultFeatureLanguage, other.DefaultFeatureLanguage) &&
        string.Equals(ConfiguredBindingCulture, other.ConfiguredBindingCulture);

    /// <summary>Determines whether <paramref name="obj"/> is a <see cref="IdeSupportConfiguration"/> with the same setting values.</summary>
    public override bool Equals(object obj)
    {
        if (ReferenceEquals(null, obj)) return false;
        if (ReferenceEquals(this, obj)) return true;
        if (obj.GetType() != GetType()) return false;
        return Equals((IdeSupportConfiguration) obj);
    }

    /// <summary>Returns a hash code derived from the configuration's setting values.</summary>
    public override int GetHashCode()
    {
        unchecked
        {
            var hashCode = ConfigurationBaseFolder != null ? ConfigurationBaseFolder.GetHashCode() : 0;
            hashCode = (hashCode * 397) ^ (Reqnroll != null ? Reqnroll.GetHashCode() : 0);
            //hashCode = (hashCode * 397) ^ (SpecFlow != null ? SpecFlow.GetHashCode() : 0);
            hashCode = (hashCode * 397) ^ (Traceability != null ? Traceability.GetHashCode() : 0);
            hashCode = (hashCode * 397) ^ (Editor != null ? Editor.GetHashCode() : 0);
            hashCode = (hashCode * 397) ^ (BindingDiscovery != null ? BindingDiscovery.GetHashCode() : 0);
            hashCode = (hashCode * 397) ^ (int) ProcessorArchitecture;
            hashCode = (hashCode * 397) ^ DebugConnector.GetHashCode();
            hashCode = (hashCode * 397) ^ (DefaultFeatureLanguage != null ? DefaultFeatureLanguage.GetHashCode() : 0);
            hashCode = (hashCode * 397) ^
                       (ConfiguredBindingCulture != null ? ConfiguredBindingCulture.GetHashCode() : 0);
            return hashCode;
        }
    }

    #endregion
}
