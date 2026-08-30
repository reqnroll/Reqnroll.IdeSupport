namespace ReqnrollConnector.SourceDiscovery;

public abstract class IdeSupportSymbolReader
{
    public abstract IEnumerable<MethodSymbolSequencePoint> ReadMethodSymbol(int token);
}
