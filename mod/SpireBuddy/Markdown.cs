using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Godot;

namespace SpireBuddy;

// Renders the Markdown subset the model replies use into panel controls:
// headings, bullet and ordered lists, block quotes, fenced code blocks,
// horizontal rules, tables, and inline **bold**, *italic*, `code`,
// ~~strike~~ and [links](https://…). Unknown syntax falls through as text.
internal static class Markdown
{
    internal static Control Render(string markdown, int fontSize = 18, Color? color = null)
    {
        var column = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        column.AddThemeConstantOverride("separation", 6);
        foreach (var block in Parse((markdown ?? "").Replace("\r\n", "\n")))
            column.AddChild(Build(block, fontSize, color));
        if (column.GetChildCount() == 0) column.AddChild(Text(Inline(""), fontSize, color));
        return column;
    }

    abstract record Block;
    sealed record Paragraph(string[] Lines) : Block;
    sealed record Quote(string[] Lines) : Block;
    sealed record Heading(int Level, string Text) : Block;
    sealed record CodeBlock(string Text) : Block;
    sealed record Rule : Block;
    sealed record Items(List<(int Level, bool Ordered, string Text)> Entries) : Block;
    sealed record Table(string[] Header, string[] Align, List<string[]> Rows) : Block;

    static readonly Regex HeadingLine = new(@"^(#{1,6})\s+(.*)$", RegexOptions.Compiled);
    static readonly Regex RuleLine = new(@"^\s*(-{3,}|\*{3,}|_{3,})\s*$", RegexOptions.Compiled);
    static readonly Regex ItemLine = new(@"^(\s*)(?:([-*+])|(\d{1,3})[.)])\s+(.*)$", RegexOptions.Compiled);
    static readonly Regex FenceLine = new(@"^\s*```", RegexOptions.Compiled);

    static List<Block> Parse(string markdown)
    {
        var lines = markdown.Split('\n');
        var blocks = new List<Block>();
        int i = 0;
        while (i < lines.Length)
        {
            var line = lines[i];
            if (line.Trim().Length == 0) { i++; continue; }
            if (FenceLine.IsMatch(line))
            {
                var body = new StringBuilder();
                i++;
                while (i < lines.Length && !FenceLine.IsMatch(lines[i])) { body.Append(lines[i]).Append('\n'); i++; }
                i++; // closing fence, or end of input
                blocks.Add(new CodeBlock(body.ToString().TrimEnd('\n')));
                continue;
            }
            var heading = HeadingLine.Match(line);
            if (heading.Success) { blocks.Add(new Heading(heading.Groups[1].Length, heading.Groups[2].Value.Trim())); i++; continue; }
            if (RuleLine.IsMatch(line)) { blocks.Add(new Rule()); i++; continue; }
            if (line.TrimStart().StartsWith('>'))
            {
                var quote = new List<string>();
                while (i < lines.Length && lines[i].TrimStart().StartsWith('>'))
                { quote.Add(lines[i].TrimStart()[1..].TrimStart()); i++; }
                blocks.Add(new Quote(quote.ToArray()));
                continue;
            }
            if (line.Contains('|') && i + 1 < lines.Length && IsDelimiterRow(lines[i + 1]))
            {
                var header = Row(line);
                var align = Alignments(lines[i + 1]);
                i += 2;
                var rows = new List<string[]>();
                while (i < lines.Length && lines[i].Contains('|') && lines[i].Trim().Length > 0)
                { rows.Add(Row(lines[i])); i++; }
                blocks.Add(new Table(header, align, rows));
                continue;
            }
            if (ItemLine.Match(line).Success)
            {
                var entries = new List<(int, bool, string)>();
                while (i < lines.Length)
                {
                    var match = ItemLine.Match(lines[i]);
                    if (match.Success)
                    {
                        // Group 2 is the bullet marker, group 3 the ordered digits.
                        entries.Add((Math.Min(3, match.Groups[1].Value.Length / 2), match.Groups[3].Value.Length > 0, match.Groups[4].Value.Trim()));
                        i++;
                    }
                    else if (entries.Count > 0 && lines[i].StartsWith(' ') && lines[i].Trim().Length > 0)
                    {
                        var last = entries[^1];
                        entries[^1] = (last.Item1, last.Item2, last.Item3 + " " + lines[i].Trim());
                        i++;
                    }
                    else break;
                }
                blocks.Add(new Items(entries));
                continue;
            }
            var paragraph = new List<string>();
            while (i < lines.Length && lines[i].Trim().Length > 0 && !FenceLine.IsMatch(lines[i])
                && !HeadingLine.IsMatch(lines[i]) && !RuleLine.IsMatch(lines[i])
                && !ItemLine.Match(lines[i]).Success && !lines[i].TrimStart().StartsWith('>'))
            { paragraph.Add(lines[i].Trim()); i++; }
            blocks.Add(new Paragraph(paragraph.ToArray()));
        }
        return blocks;
    }

    static bool IsDelimiterRow(string line)
    {
        if (!line.Contains('-') || !line.Contains('|')) return false;
        return Row(line).All(cell => Regex.IsMatch(cell, @"^:?-+:?$"));
    }

    static string[] Row(string line)
    {
        var text = line.Trim().Trim('|');
        var cells = new List<string>();
        var current = new StringBuilder();
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\\' && i + 1 < text.Length && text[i + 1] == '|') { current.Append('|'); i++; }
            else if (text[i] == '|') { cells.Add(current.ToString().Trim()); current.Clear(); }
            else current.Append(text[i]);
        }
        cells.Add(current.ToString().Trim());
        return [.. cells];
    }

    static string[] Alignments(string delimiter)
    {
        return Row(delimiter).Select(cell => cell.StartsWith(':') && cell.EndsWith(':') ? "center"
            : cell.EndsWith(':') ? "right" : "left").ToArray();
    }

    static Node Build(Block block, int size, Color? color)
    {
        switch (block)
        {
            case CodeBlock code:
                return CodePanel("[code]" + CodeColor(Escape(code.Text)) + "[/code]", size, new Color("c9d6e5"));
            case Heading h:
                var scale = h.Level switch { 1 => 8, 2 => 5, 3 => 2, _ => 0 };
                return Text($"[font_size={size + scale}][b]{Inline(h.Text)}[/b][/font_size]", size, color);
            case Rule:
                return new HSeparator { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            case Quote q:
                var panel = new PanelContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
                panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
                {
                    BgColor = new Color("ffffff0a"),
                    BorderColor = new Color("5c897f"),
                    BorderWidthLeft = 3,
                    ContentMarginLeft = 10, ContentMarginRight = 10, ContentMarginTop = 6, ContentMarginBottom = 6,
                });
                panel.AddChild(Text(string.Join("\n", q.Lines.Select(Inline)), size, color ?? new Color("9fb4c7")));
                return panel;
            case Items items:
                var body = new StringBuilder();
                var counters = new Dictionary<int, int>();
                foreach (var (level, ordered, text) in items.Entries)
                {
                    if (ordered) counters[level] = counters.GetValueOrDefault(level) + 1;
                    else counters[level] = 0;
                    var marker = ordered ? counters[level] + ". " : "• ";
                    // Godot's indent tag takes no value: the bare tag indents one
                    // level per occurrence, so nesting repeats it.
                    var open = string.Concat(Enumerable.Repeat("[indent]", level));
                    var close = string.Concat(Enumerable.Repeat("[/indent]", level));
                    body.Append($"{open}{marker}{Inline(text)}\n{close}");
                }
                return Text(body.ToString(), size, color);
            case Table table:
                return TableBlock(table, size, color);
            case Paragraph p:
                return Text(string.Join("\n", p.Lines.Select(Inline)), size, color);
            default:
                return Text("", size, color);
        }
    }

    static Control TableBlock(Table table, int size, Color? color)
    {
        var columns = table.Header.Length;
        var grid = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        grid.AddThemeConstantOverride("separation", 4);
        var headerRow = new HBoxContainer();
        for (int c = 0; c < columns; c++)
            headerRow.AddChild(Cell(c < table.Header.Length ? table.Header[c] : "", AlignOf(table.Align, c), size - 1, header: true));
        grid.AddChild(headerRow);
        var separator = new HSeparator { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        separator.AddThemeConstantOverride("separation", 4);
        grid.AddChild(separator);
        foreach (var row in table.Rows)
        {
            var line = new HBoxContainer();
            for (int c = 0; c < columns; c++)
                line.AddChild(Cell(c < row.Length ? row[c] : "", AlignOf(table.Align, c), size - 1, header: false));
            grid.AddChild(line);
        }
        var panel = new PanelContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color("101a2799"),
            CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6, CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6,
            ContentMarginLeft = 10, ContentMarginRight = 10, ContentMarginTop = 8, ContentMarginBottom = 8,
        });
        panel.AddChild(grid);
        return panel;
    }

    static string AlignOf(string[] align, int column) => column < align.Length ? align[column] : "left";

    static RichTextLabel Cell(string text, string align, int size, bool header)
    {
        var body = header ? "[b]" + Inline(text) + "[/b]" : Inline(text);
        if (align == "right") body = "[right]" + body + "[/right]";
        else if (align == "center") body = "[center]" + body + "[/center]";
        var label = Text(body, size, header ? new Color("a8d7cf") : null);
        label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        return label;
    }

    static Control CodePanel(string bbcode, int size, Color color)
    {
        var panel = new PanelContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color("0d141ecc"),
            CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6, CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6,
            ContentMarginLeft = 10, ContentMarginRight = 10, ContentMarginTop = 8, ContentMarginBottom = 8,
        });
        panel.AddChild(Text(bbcode, Math.Max(12, size - 2), color));
        return panel;
    }

    static RichTextLabel Text(string bbcode, int size, Color? color)
    {
        var label = new RichTextLabel
        {
            BbcodeEnabled = true,
            FitContent = true,
            ScrollActive = false,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        label.AddThemeFontSizeOverride("normal_font_size", size);
        label.AddThemeFontSizeOverride("bold_font_size", size);
        label.AddThemeFontSizeOverride("italics_font_size", size);
        if (color != null) label.AddThemeColorOverride("default_color", color.Value);
        label.Text = bbcode;
        label.MetaClicked += meta => { try { OS.ShellOpen(meta.ToString()); } catch (Exception) { } };
        return label;
    }

    static readonly Regex CodeSpan = new(@"`([^`\n]+)`", RegexOptions.Compiled);
    static readonly Regex Link = new(@"\[([^\]\n]+)\]\((https?://[^)\s]+)\)", RegexOptions.Compiled);
    static readonly Regex BareUrl = new(@"(?<![\w/=])(https?://[^\s<>\[\]""'']+[^\s<>\[\]""''.,;:!?])", RegexOptions.Compiled);
    static readonly Regex BoldItalic = new(@"\*\*\*(?<t>[^*\n]+)\*\*\*", RegexOptions.Compiled);
    static readonly Regex Bold = new(@"\*\*(?<t>[^*\n]+)\*\*|__(?<t>[^_\n]+)__", RegexOptions.Compiled);
    static readonly Regex Italic = new(@"(?<![\w*])\*(?<t>[^*\n]+)\*(?![\w*])|(?<![\w_])_(?<t>[^_\n]+)_(?![\w_])", RegexOptions.Compiled);
    static readonly Regex Strike = new(@"~~(?<t>[^~\n]+)~~", RegexOptions.Compiled);

    // Inline conversion order: protect code spans and links, escape brackets,
    // apply emphasis on what remains, then splice the protected tokens back.
    internal static string Inline(string text)
    {
        var tokens = new List<string>();
        string Protect(string bbcode) { tokens.Add(bbcode); return "\x00" + (tokens.Count - 1) + "\x00"; }
        text = CodeSpan.Replace(text, m => Protect("[code]" + CodeColor(Escape(m.Groups[1].Value)) + "[/code]"));
        text = Link.Replace(text, m => Protect($"[url={m.Groups[2].Value}]{Escape(m.Groups[1].Value)}[/url]"));
        text = BareUrl.Replace(text, m => Protect($"[url={m.Groups[1].Value}]{Escape(m.Groups[1].Value)}[/url]"));
        text = Escape(text);
        text = BoldItalic.Replace(text, "[b][i]${t}[/i][/b]");
        text = Bold.Replace(text, "[b]${t}[/b]");
        text = Italic.Replace(text, "[i]${t}[/i]");
        text = Strike.Replace(text, "[s]${t}[/s]");
        for (int i = 0; i < tokens.Count; i++) text = text.Replace("\x00" + i + "\x00", tokens[i]);
        return text;
    }

    // A message is model output rendered as BBCode; brackets that are not ours
    // must survive verbatim.
    static string Escape(string text) => text.Replace("[", "[lb]");

    static string CodeColor(string escaped) => "[color=#8fd3c7]" + escaped + "[/color]";
}
