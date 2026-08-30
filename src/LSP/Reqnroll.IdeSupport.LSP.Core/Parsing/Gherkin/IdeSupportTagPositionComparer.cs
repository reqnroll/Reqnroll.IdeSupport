namespace Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;

internal class IdeSupportTagPositionComparer : IComparer<IdeSupportTag>
{
    public int Compare(IdeSupportTag t1, IdeSupportTag t2)
    {
        if (ReferenceEquals(t1, t2)) return 0;
        if (ReferenceEquals(null, t2)) return 1;
        if (ReferenceEquals(null, t1)) return -1;
        var order = t1.Range.Start.CompareTo(t2.Range.Start);
        if (order != 0) return order;
        order = t1.Range.End.CompareTo(t2.Range.End);
        if (order != 0) return order;
        order = string.Compare(t1.Type, t2.Type, StringComparison.Ordinal);
        return order;
    }
}
