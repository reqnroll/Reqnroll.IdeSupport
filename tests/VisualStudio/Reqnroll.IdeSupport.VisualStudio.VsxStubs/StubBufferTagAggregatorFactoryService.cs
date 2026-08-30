#if false // Deferred: IdeSupportTag tagger not yet ported to VS layer
namespace Reqnroll.VisualStudio.VsxStubs;

public class StubBufferTagAggregatorFactoryService : IBufferTagAggregatorFactoryService
{
    private readonly ITaggerProvider _taggerProvider;

    public StubBufferTagAggregatorFactoryService(ITaggerProvider taggerProvider)
    {
        _taggerProvider = taggerProvider;
    }

    public ITagAggregator<T> CreateTagAggregator<T>(ITextBuffer textBuffer) where T : ITag =>
        CreateTagAggregator<T>(textBuffer, TagAggregatorOptions.None);

    public ITagAggregator<T> CreateTagAggregator<T>(ITextBuffer textBuffer, TagAggregatorOptions options) where T : ITag
    {
        if (typeof(T) == typeof(IdeSupportTag))
        {
            var tagger = _taggerProvider.CreateTagger<IdeSupportTag>(textBuffer);

            return new StubTagAggregator<T>((ITagger<T>) tagger,
                VsxStubObjects.BufferGraphFactoryService.CreateBufferGraph(textBuffer));
        }

        throw new NotSupportedException();
    }
}

#endif
