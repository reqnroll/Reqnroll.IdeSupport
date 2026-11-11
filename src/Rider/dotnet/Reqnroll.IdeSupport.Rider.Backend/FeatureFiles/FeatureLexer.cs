using JetBrains.ReSharper.Psi.Parsing;
using JetBrains.Text;

namespace Reqnroll.IdeSupport.Rider.Backend.FeatureFiles;

/// <summary>Lexer that turns the whole buffer into one <see cref="FeatureTokenType.Text"/> token.</summary>
public class FeatureLexer : ILexer
{
    private readonly struct Position
    {
        public Position(int start, int end, bool hasToken)
        {
            Start = start;
            End = end;
            HasToken = hasToken;
        }

        public int Start { get; }
        public int End { get; }
        public bool HasToken { get; }
    }

    private int _tokenStart;
    private int _tokenEnd;
    private bool _hasToken;

    public FeatureLexer(IBuffer buffer)
    {
        Buffer = buffer;
    }

    public IBuffer Buffer { get; }

    public TokenNodeType TokenType => _hasToken ? FeatureTokenType.Text : null;

    public int TokenStart => _tokenStart;

    public int TokenEnd => _tokenEnd;

    public uint State => 0;

    public object CurrentPosition
    {
        get => new Position(_tokenStart, _tokenEnd, _hasToken);
        set
        {
            var position = (Position)value;
            _tokenStart = position.Start;
            _tokenEnd = position.End;
            _hasToken = position.HasToken;
        }
    }

    public void Start()
    {
        _tokenStart = 0;
        _tokenEnd = 0;
        _hasToken = false;
        Advance();
    }

    public void Advance()
    {
        if (_tokenEnd >= Buffer.Length)
        {
            _tokenStart = Buffer.Length;
            _hasToken = false;
            return;
        }

        _tokenStart = _tokenEnd;
        _tokenEnd = Buffer.Length;
        _hasToken = true;
    }
}
