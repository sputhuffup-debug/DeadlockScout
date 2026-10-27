using System.Text;

namespace DeadlockScout.Steam.Vdf;

/// <summary>A node of Valve's KeyValues text format: either a string value or an ordered map of children.</summary>
public sealed class VdfNode
{
    private readonly List<KeyValuePair<string, VdfNode>> _children = new();

    public VdfNode(string value) { Value = value; }
    public VdfNode() { }

    public string? Value { get; set; }
    public bool IsObject => Value is null;
    public IReadOnlyList<KeyValuePair<string, VdfNode>> Children => _children;

    public VdfNode? this[string key]
    {
        get
        {
            foreach (var kv in _children)
                if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)) return kv.Value;
            return null;
        }
    }

    public void Add(string key, VdfNode child) => _children.Add(new(key, child));

    /// <summary>Sets a string value, replacing an existing key (case-insensitive) or appending.</summary>
    public void Set(string key, string value)
    {
        for (int i = 0; i < _children.Count; i++)
        {
            if (string.Equals(_children[i].Key, key, StringComparison.OrdinalIgnoreCase))
            {
                _children[i] = new(_children[i].Key, new VdfNode(value));
                return;
            }
        }
        _children.Add(new(key, new VdfNode(value)));
    }

    /// <summary>Returns the child object at <paramref name="key"/>, creating it when missing.</summary>
    public VdfNode GetOrAddObject(string key)
    {
        var existing = this[key];
        if (existing is { IsObject: true }) return existing;
        var node = new VdfNode();
        if (existing is null) _children.Add(new(key, node));
        else
        {
            for (int i = 0; i < _children.Count; i++)
                if (string.Equals(_children[i].Key, key, StringComparison.OrdinalIgnoreCase)) _children[i] = new(_children[i].Key, node);
        }
        return node;
    }

    /// <summary>Walks a case-insensitive path like "UserLocalConfigStore/Software/Valve/Steam/apps/1422450".</summary>
    public VdfNode? Find(params string[] path)
    {
        var node = this;
        foreach (var p in path)
        {
            node = node[p];
            if (node is null) return null;
        }
        return node;
    }

    public string? GetString(params string[] path) => Find(path)?.Value;

    public string Serialize()
    {
        var sb = new StringBuilder();
        foreach (var kv in _children) Write(sb, kv.Key, kv.Value, 0);
        return sb.ToString();
    }

    private static void Write(StringBuilder sb, string key, VdfNode node, int depth)
    {
        sb.Append('\t', depth).Append('"').Append(Escape(key)).Append('"');
        if (node.IsObject)
        {
            sb.Append('\n').Append('\t', depth).Append("{\n");
            foreach (var kv in node._children) Write(sb, kv.Key, kv.Value, depth + 1);
            sb.Append('\t', depth).Append("}\n");
        }
        else
        {
            sb.Append("\t\t\"").Append(Escape(node.Value!)).Append("\"\n");
        }
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
