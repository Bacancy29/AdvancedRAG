using System.Text;

namespace Rag.Ingestion.Chunking;

/// <summary>Continuous text plus a map from character offset to page number.</summary>
internal sealed class FlowBuffer
{
    private readonly StringBuilder _sb = new();
    private readonly List<(int Offset, int Page)> _pages = [];

    public bool IsEmpty => _sb.ToString().Trim().Length == 0;

    public void Append(string text, int page)
    {
        if (_sb.Length > 0) _sb.Append(' ');
        _pages.Add((_sb.Length, page));
        _sb.Append(text);
    }

    public int PageAt(int offset)
    {
        var page = _pages.Count > 0 ? _pages[0].Page : 0;
        foreach (var (o, p) in _pages)
        {
            if (o > offset) break;
            page = p;
        }
        return page;
    }

    public void Clear()
    {
        _sb.Clear();
        _pages.Clear();
    }

    public override string ToString() => _sb.ToString();
}
