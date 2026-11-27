namespace ReqnrollConnector.SourceDiscovery;

public abstract class IdeSupportSymbolReader
{
    public abstract IEnumerable<MethodSymbolSequencePoint> ReadMethodSymbol(int token);

    /// <summary>
    /// Returns the exact identity of the method with <paramref name="token"/>, or <see langword="null"/>
    /// when it cannot be determined or the method's declaring type is not <paramref name="expectedDeclaringType"/>
    /// (the reflection full name, with <c>+</c> for nesting) — the guard against a token resolved in the wrong module.
    /// </summary>
    public virtual Reqnroll.IdeSupport.LSP.Connector.Models.MethodIdentity? GetMethodIdentity(int token, string? expectedDeclaringType) => null;
}
