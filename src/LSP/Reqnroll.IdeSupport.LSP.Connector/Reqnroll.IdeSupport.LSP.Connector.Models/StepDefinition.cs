#nullable disable

namespace Reqnroll.IdeSupport.LSP.Connector.Models;

/// <summary>StepDefinition</summary>
public class StepDefinition
{
    /// <summary>Gets or sets the type.</summary>
    public string Type { get; set; }
    /// <summary>Gets or sets the regex.</summary>
    public string Regex { get; set; }
    /// <summary>Gets or sets the method.</summary>
    public string Method { get; set; }
    /// <summary>Gets or sets the param types.</summary>
    public string ParamTypes { get; set; }
    /// <summary>Gets or sets the scope.</summary>
    public StepScope Scope { get; set; }

    /// <summary>Gets or sets the expression.</summary>
    public string Expression { get; set; }
    /// <summary>Gets or sets the error.</summary>
    public string Error { get; set; }

    /// <summary>Gets or sets the source location.</summary>
    public string SourceLocation { get; set; }

    /// <summary>
    /// The exact identity of the bound method, when the connector could determine it; <see langword="null"/>
    /// from connectors that predate it or for methods it cannot describe (e.g. generic ones).
    /// </summary>
    public MethodIdentity MethodIdentity { get; set; }

    #region Equality

    /// <summary>Gets or sets the equals.</summary>
    protected bool Equals(StepDefinition other) => Type == other.Type && Regex == other.Regex &&
                                                   Method == other.Method && ParamTypes == other.ParamTypes &&
                                                   Equals(Scope, other.Scope) && Expression == other.Expression &&
                                                   Error == other.Error && SourceLocation == other.SourceLocation &&
                                                   Equals(MethodIdentity, other.MethodIdentity);

    /// <summary>Gets or sets the equals.</summary>
    public override bool Equals(object obj)
    {
        if (ReferenceEquals(null, obj)) return false;
        if (ReferenceEquals(this, obj)) return true;
        if (obj.GetType() != GetType()) return false;
        return Equals((StepDefinition) obj);
    }

    /// <summary>Gets or sets the get hash code.</summary>
    public override int GetHashCode()
    {
        unchecked
        {
            var hashCode = Type != null ? Type.GetHashCode() : 0;
            hashCode = (hashCode * 397) ^ (Regex != null ? Regex.GetHashCode() : 0);
            hashCode = (hashCode * 397) ^ (Method != null ? Method.GetHashCode() : 0);
            hashCode = (hashCode * 397) ^ (ParamTypes != null ? ParamTypes.GetHashCode() : 0);
            hashCode = (hashCode * 397) ^ (Scope != null ? Scope.GetHashCode() : 0);
            hashCode = (hashCode * 397) ^ (Expression != null ? Expression.GetHashCode() : 0);
            hashCode = (hashCode * 397) ^ (Error != null ? Error.GetHashCode() : 0);
            hashCode = (hashCode * 397) ^ (SourceLocation != null ? SourceLocation.GetHashCode() : 0);
            hashCode = (hashCode * 397) ^ (MethodIdentity != null ? MethodIdentity.GetHashCode() : 0);
            return hashCode;
        }
    }

    #endregion
}
