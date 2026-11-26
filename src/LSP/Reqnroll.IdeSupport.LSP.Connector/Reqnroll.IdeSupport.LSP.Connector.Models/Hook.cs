#nullable disable

namespace Reqnroll.IdeSupport.LSP.Connector.Models;

/// <summary>Hook</summary>
public class Hook
{
    /// <summary>Gets or sets the type.</summary>
    public string Type { get; set; }
    /// <summary>Gets or sets the hook order.</summary>
    public int? HookOrder { get; set; }
    /// <summary>Gets or sets the method.</summary>
    public string Method { get; set; }
    //public string ParamTypes { get; set; }
    /// <summary>Gets or sets the scope.</summary>
    public StepScope Scope { get; set; }

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
    protected bool Equals(Hook other)
    {
        return Type == other.Type && HookOrder == other.HookOrder && Method == other.Method && Equals(Scope, other.Scope) && Error == other.Error && SourceLocation == other.SourceLocation &&
               Equals(MethodIdentity, other.MethodIdentity);
    }

    /// <summary>Gets or sets the equals.</summary>
    public override bool Equals(object obj)
    {
        if (ReferenceEquals(null, obj)) return false;
        if (ReferenceEquals(this, obj)) return true;
        if (obj.GetType() != this.GetType()) return false;
        return Equals((Hook)obj);
    }

    /// <summary>Gets or sets the get hash code.</summary>
    public override int GetHashCode()
    {
        unchecked
        {
            var hashCode = (Type != null ? Type.GetHashCode() : 0);
            hashCode = (hashCode * 397) ^ HookOrder.GetHashCode();
            hashCode = (hashCode * 397) ^ (Method != null ? Method.GetHashCode() : 0);
            hashCode = (hashCode * 397) ^ (Scope != null ? Scope.GetHashCode() : 0);
            hashCode = (hashCode * 397) ^ (SourceLocation != null ? SourceLocation.GetHashCode() : 0);
            hashCode = (hashCode * 397) ^ (MethodIdentity != null ? MethodIdentity.GetHashCode() : 0);
            return hashCode;
        }
    }

    #endregion
}
