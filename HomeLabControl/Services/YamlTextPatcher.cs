using System.Text;

namespace HomeLabControl.Services;

/// <summary>
/// Точечная правка YAML-текста с сохранением комментариев и форматирования: заменяется только блок
/// одного ключа (по пути из ключей мапы, с элементами списка по имени), всё остальное остаётся как было.
/// Понимает блочный YAML, который пишет человек и YamlDotNet (отступы пробелами, списки "- ").
/// </summary>
public static class YamlTextPatcher
{
    /// <summary>Сегмент пути: ключ мапы или элемент списка с name: Name.</summary>
    public readonly record struct Segment(string Key, string? ItemName = null)
    {
        public override string ToString() => ItemName == null ? Key : $"{Key}[{ItemName}]";
    }

    private record Line(int Indent, string Text)
    {
        public string Content => Text.TrimStart();
        public bool IsStructural => Content.Length > 0 && !Content.StartsWith('#');
    }

    /// <summary>
    /// Установить значение по пути: value — YAML значения с нулевым отступом (вывод сериализатора),
    /// null — удалить ключ (или элемент списка, если последний сегмент — элемент).
    /// </summary>
    public static string Set(string text, IReadOnlyList<Segment> path, string? value)
    {
        var newline = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = text.Replace("\r\n", "\n").Split('\n').Select(ToLine).ToList();
        if (lines.Count > 0 && lines[^1].Text.Length == 0)
            lines.RemoveAt(lines.Count - 1);

        SetIn(lines, 0, lines.Count, -1, path, 0, value);
        return string.Join(newline, lines.Select(l => l.Text)) + newline;
    }

    private static Line ToLine(string s) => new(s.Length - s.TrimStart(' ').Length, s);

    // ─── Поиск ────────────────────────────────────────────────────────────────

    /// <summary>Отступ детей в диапазоне [from, to) — минимальный отступ структурной строки (или null — детей нет).</summary>
    private static int? ChildIndent(List<Line> lines, int from, int to)
    {
        int? indent = null;
        for (var i = from; i < to; i++)
            if (lines[i].IsStructural && (indent == null || lines[i].Indent < indent))
                indent = lines[i].Indent;
        return indent;
    }

    private static bool IsKeyLine(Line line, int indent, string key)
        => line.IsStructural && line.Indent == indent &&
           (line.Content.StartsWith(key + ":") && (line.Content.Length == key.Length + 1 || line.Content[key.Length + 1] is ' ' or '\t'));

    /// <summary>Конец блока строки start: до следующей структурной строки с отступом ≤ indent; хвостовые комментарии — следующему.</summary>
    private static int BlockEnd(List<Line> lines, int start, int to, int indent, bool listItem = false)
    {
        var end = to;
        for (var i = start + 1; i < to; i++)
        {
            var l = lines[i];
            if (!l.IsStructural)
                continue;
            if (l.Indent < indent || (l.Indent == indent && (!listItem || l.Content.StartsWith("- ") || l.Content == "-")))
            {
                end = i;
                break;
            }
        }

        // Пустые строки и комментарии перед следующим ключом (на его уровне или выше) остаются ему
        while (end > start + 1 && !lines[end - 1].IsStructural && (lines[end - 1].Content.Length == 0 || lines[end - 1].Indent <= indent))
            end--;
        return end;
    }

    // ─── Установка ────────────────────────────────────────────────────────────

    /// <summary>[from, to) — тело родителя (без его строки), parentIndent — отступ родителя (-1 — корень).</summary>
    private static void SetIn(List<Line> lines, int from, int to, int parentIndent, IReadOnlyList<Segment> path, int depth, string? value)
    {
        var seg = path[depth];
        var last = depth == path.Count - 1;
        var indent = ChildIndent(lines, from, to) ?? parentIndent + 2;
        if (parentIndent < 0 && ChildIndent(lines, from, to) is null)
            indent = 0;

        var keyLine = -1;
        for (var i = from; i < to; i++)
        {
            if (IsKeyLine(lines[i], indent, seg.Key))
            {
                keyLine = i;
                break;
            }
        }

        if (keyLine < 0)
        {
            if (value == null)
                return; // удалять нечего
            var insertAt = InsertPoint(lines, from, to);
            lines.InsertRange(insertAt, Build(seg.Key, path, depth, value, indent));
            return;
        }

        var end = BlockEnd(lines, keyLine, to, indent);

        if (seg.ItemName == null)
        {
            if (last)
            {
                // Комментарий в конце строки ("key: value   # …") переносим, если значение осталось однострочным
                var inlineComment = end == keyLine + 1 ? InlineComment(lines[keyLine].Content, seg.Key) : null;
                lines.RemoveRange(keyLine, end - keyLine);
                if (value != null)
                {
                    var newLines = KeyLines(seg.Key, value, indent, indent + 2).ToList();
                    if (inlineComment != null && newLines.Count == 1)
                        newLines[0] = newLines[0] with { Text = newLines[0].Text + inlineComment };
                    lines.InsertRange(keyLine, newLines);
                }
                return;
            }

            // Ключ записан строкой ("key: {}" / "key: []") — заменяем целиком
            if (lines[keyLine].Content.Length > seg.Key.Length + 1 && end == keyLine + 1)
            {
                lines.RemoveAt(keyLine);
                lines.InsertRange(keyLine, Build(seg.Key, path, depth, value, indent));
                return;
            }

            SetIn(lines, keyLine + 1, end, indent, path, depth + 1, value);
            return;
        }

        // Элемент списка с name: ItemName
        var itemIndent = ChildIndent(lines, keyLine + 1, end) ?? indent + 2;
        var (itemStart, itemEnd) = FindItem(lines, keyLine + 1, end, itemIndent, seg.ItemName);

        if (itemStart < 0)
        {
            if (value == null)
                return;
            if (!last)
                throw new InvalidOperationException($"YAML patch: item {seg} not found");
            var insertAt = InsertPoint(lines, keyLine + 1, end);
            lines.InsertRange(insertAt, ItemLines(value, itemIndent));
            return;
        }

        if (last)
        {
            lines.RemoveRange(itemStart, itemEnd - itemStart);
            if (value != null)
                lines.InsertRange(itemStart, ItemLines(value, itemIndent));
            return;
        }

        // Внутрь элемента: тело — строки элемента, где "- " считается отступом (itemIndent + 2)
        var bodyIndent = itemIndent + 2;
        var first = lines[itemStart];
        var rest = first.Content.Length > 2 ? first.Content[2..] : "";
        // Разворачиваем "- key: v" в "-" + "  key: v", чтобы первый ключ правился так же, как остальные
        lines[itemStart] = new Line(itemIndent, new string(' ', itemIndent) + "-");
        lines.Insert(itemStart + 1, new Line(bodyIndent, new string(' ', bodyIndent) + rest));
        itemEnd++;

        var before = lines.Count;
        SetIn(lines, itemStart + 1, itemEnd, itemIndent, path, depth + 1, value);
        itemEnd += lines.Count - before;

        // Сворачиваем обратно "-" + "  key: v" → "- key: v"
        if (itemStart + 1 < itemEnd && lines[itemStart + 1].IsStructural && lines[itemStart + 1].Indent == bodyIndent)
        {
            lines[itemStart] = new Line(itemIndent, new string(' ', itemIndent) + "- " + lines[itemStart + 1].Content);
            lines.RemoveAt(itemStart + 1);
        }
    }

    private static (int Start, int End) FindItem(List<Line> lines, int from, int to, int itemIndent, string name)
    {
        for (var i = from; i < to; i++)
        {
            var l = lines[i];
            if (!l.IsStructural || l.Indent != itemIndent || !(l.Content.StartsWith("- ") || l.Content == "-"))
                continue;

            var end = BlockEnd(lines, i, to, itemIndent, listItem: true);
            if (ItemName(lines, i, end, itemIndent) is { } n && n.Equals(name, StringComparison.Ordinal))
                return (i, end);
            i = end - 1;
        }
        return (-1, -1);
    }

    private static string? ItemName(List<Line> lines, int start, int end, int itemIndent)
    {
        string? Value(string content) => content.StartsWith("name:") ? Unquote(content[5..].Trim()) : null;

        var first = lines[start].Content;
        if (first.StartsWith("- ") && Value(first[2..].TrimStart()) is { } v)
            return v;
        for (var i = start + 1; i < end; i++)
            if (lines[i].IsStructural && lines[i].Indent == itemIndent + 2 && Value(lines[i].Content) is { } v2)
                return v2;
        return null;
    }

    /// <summary>"key: value   # comment" → "   # comment" (с пробелами перед #); null — комментария нет.</summary>
    private static string? InlineComment(string content, string key)
    {
        var rest = content[(key.Length + 1)..];
        var inQuote = '\0';
        for (var i = 0; i < rest.Length; i++)
        {
            var c = rest[i];
            if (inQuote != '\0')
            {
                if (c == inQuote)
                    inQuote = '\0';
                continue;
            }
            if (c is '"' or '\'')
                inQuote = c;
            else if (c == '#' && i > 0 && rest[i - 1] is ' ' or '\t')
            {
                var start = i;
                while (start > 0 && rest[start - 1] is ' ' or '\t')
                    start--;
                return rest[start..];
            }
        }
        return null;
    }

    private static string Unquote(string s)
    {
        var hash = s.IndexOf(" #", StringComparison.Ordinal);
        if (hash >= 0 && !s.StartsWith('"') && !s.StartsWith('\''))
            s = s[..hash].TrimEnd();
        return s.Length >= 2 && (s[0] == '"' && s[^1] == '"' || s[0] == '\'' && s[^1] == '\'') ? s[1..^1] : s;
    }

    /// <summary>Куда вставлять новый ключ / элемент: после последней строки тела, перед хвостовыми пустыми строками.</summary>
    private static int InsertPoint(List<Line> lines, int from, int to)
    {
        var at = to;
        while (at > from && lines[at - 1].Content.Length == 0)
            at--;
        return at;
    }

    // ─── Генерация ────────────────────────────────────────────────────────────

    private static List<string> ValueLines(string value)
        => value.Replace("\r\n", "\n").TrimEnd('\n').Split('\n').ToList();

    /// <summary>"key: …" с отступом indent; составное значение — со следующей строки с отступом childIndent.</summary>
    private static IEnumerable<Line> KeyLines(string key, string value, int indent, int childIndent)
    {
        var v = ValueLines(value);
        var pad = new string(' ', indent);
        if (v.Count == 1 && !IsBlockStart(v[0]))
        {
            yield return new Line(indent, $"{pad}{key}: {v[0]}");
            yield break;
        }

        yield return new Line(indent, $"{pad}{key}:");
        var childPad = new string(' ', childIndent);
        foreach (var l in v)
            yield return ToLine(l.Length == 0 ? "" : childPad + l);
    }

    /// <summary>Мапа или список блоком (а не скаляр / flow-значение в одну строку).</summary>
    private static bool IsBlockStart(string firstLine)
        => firstLine.StartsWith("- ") || firstLine == "-" ||
           (firstLine.Contains(": ") || firstLine.EndsWith(':')) && !firstLine.StartsWith('{') && !firstLine.StartsWith('[') && !firstLine.StartsWith('"') && !firstLine.StartsWith('\'');

    private static IEnumerable<Line> ItemLines(string value, int itemIndent)
    {
        var v = ValueLines(value);
        for (var i = 0; i < v.Count; i++)
        {
            var prefix = new string(' ', itemIndent) + (i == 0 ? "- " : "  ");
            yield return ToLine(v[i].Length == 0 ? "" : prefix + v[i]);
        }
    }

    /// <summary>Недостающие ключи пути от depth: строим вложенные мапы вокруг значения.</summary>
    private static List<Line> Build(string key, IReadOnlyList<Segment> path, int depth, string? value, int indent)
    {
        if (path.Skip(depth + 1).Any(s => s.ItemName != null) || path[depth].ItemName != null)
            throw new InvalidOperationException($"YAML patch: cannot create list item path {string.Join(".", path)}");

        var sb = new StringBuilder();
        for (var d = depth + 1; d < path.Count; d++)
        {
            var pad = new string(' ', (d - depth - 1) * 2);
            if (d == path.Count - 1)
            {
                var v = ValueLines(value!);
                if (v.Count == 1 && !IsBlockStart(v[0]))
                    sb.Append(pad).Append(path[d].Key).Append(": ").Append(v[0]).Append('\n');
                else
                {
                    sb.Append(pad).Append(path[d].Key).Append(":\n");
                    foreach (var l in v)
                        sb.Append(pad).Append("  ").Append(l).Append('\n');
                }
            }
            else
            {
                sb.Append(pad).Append(path[d].Key).Append(":\n");
            }
        }

        var inner = depth == path.Count - 1 ? value! : sb.ToString();
        return KeyLines(key, inner, indent, indent + 2).ToList();
    }
}
