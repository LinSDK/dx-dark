using System.Text;

namespace DxDark.Core.Settings;

/// <summary>One [section] of an INI file: ordered key = value lines and comments.</summary>
public sealed class IniSection(string name)
{
    private readonly List<(string? Key, string Text)> _lines = [];

    public string Name { get; } = name;

    /// <summary>The value of <paramref name="key"/> (case-insensitive; the last one wins), or null.</summary>
    public string? Get(string key)
    {
        for (int i = _lines.Count - 1; i >= 0; i--)
        {
            if (_lines[i].Key is { } k && string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
            {
                return _lines[i].Text;
            }
        }

        return null;
    }

    public IniSection Set(string key, string value)
    {
        _lines.Add((key, value));
        return this;
    }

    public IniSection Comment(string text)
    {
        _lines.Add((null, text));
        return this;
    }

    internal void Write(StringBuilder text)
    {
        text.Append('[').Append(Name).Append(']').AppendLine();
        foreach ((string? key, string value) in _lines)
        {
            if (key is null)
            {
                text.Append("; ").AppendLine(value);
            }
            else
            {
                text.Append(key).Append(" = ").AppendLine(Quote(value));
            }
        }
    }

    // Values keep leading/trailing spaces only when quoted.
    private static string Quote(string value) =>
        value.Length > 0 && (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1]) || value[0] == '"')
            ? $"\"{value}\""
            : value;
}

/// <summary>
/// A plain INI file: [sections], key = value lines, and comment lines starting with ; or #.
/// Keys and section names are case-insensitive; unknown ones are ignored when reading.
/// </summary>
public sealed class IniDocument
{
    private readonly List<string> _header = [];

    public List<IniSection> Sections { get; } = [];

    public IniSection? Find(string name) =>
        Sections.LastOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

    public IniSection Add(string name)
    {
        var section = new IniSection(name);
        Sections.Add(section);
        return section;
    }

    /// <summary>A comment line at the top of the file.</summary>
    public IniDocument HeaderComment(string text)
    {
        _header.Add(text);
        return this;
    }

    public static IniDocument Parse(string text)
    {
        var document = new IniDocument();
        IniSection? section = null;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim().TrimStart('﻿');
            if (line.Length == 0 || line[0] is ';' or '#')
            {
                continue;
            }

            if (line[0] == '[')
            {
                int close = line.IndexOf(']');
                section = document.Add((close > 0 ? line[1..close] : line[1..]).Trim());
                continue;
            }

            int equals = line.IndexOf('=');
            if (equals <= 0 || section is null)
            {
                continue;
            }

            string key = line[..equals].Trim();
            string value = line[(equals + 1)..].Trim();
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            {
                value = value[1..^1];
            }

            section.Set(key, value);
        }

        return document;
    }

    public override string ToString()
    {
        var text = new StringBuilder();
        foreach (string line in _header)
        {
            text.Append("; ").AppendLine(line);
        }

        foreach (IniSection section in Sections)
        {
            text.AppendLine();
            section.Write(text);
        }

        return text.ToString();
    }
}
