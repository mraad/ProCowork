using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace ArcGISClaude.UI
{
    /// <summary>
    /// Converts the engine's Markdown prose into the small HTML subset that
    /// <see cref="HtmlPresenter"/> renders (headings, p, strong/em, code, pre, ul/ol/li,
    /// a, table). The engine keeps emitting Markdown — its natural, higher-quality
    /// format — and the panel converts here so the output is rendered as HTML.
    ///
    /// Emphasis is only '*'/'**' (not '_'), so snake_case identifiers such as
    /// run_python_current and arcpy.da are never mangled into italics. The converter is
    /// tolerant of half-streamed Markdown (e.g. an unclosed code fence) because it runs
    /// on every streaming update.
    /// </summary>
    internal static class MarkdownToHtml
    {
        // Inline: **bold**, *italic*, `code`, [text](url). Matches the renderer's old set.
        private static readonly Regex InlineRx = new Regex(
            @"\*\*(?<b>[^*]+?)\*\*|\*(?<i>[^*]+?)\*|`(?<c>[^`]+?)`|\[(?<t>[^\]]+?)\]\((?<u>[^)]+?)\)",
            RegexOptions.Compiled);

        // Per-line block patterns, compiled once (they run on every line of every
        // streaming update rather than being recompiled/looked-up per call).
        private static readonly Regex HeaderRx = new Regex(@"^(#{1,6})\s+(.*)$", RegexOptions.Compiled);
        private static readonly Regex BulletRx = new Regex(@"^\s*[-*+]\s+(.*)$", RegexOptions.Compiled);
        private static readonly Regex NumberedRx = new Regex(@"^\s*(\d+)\.\s+(.*)$", RegexOptions.Compiled);
        private static readonly Regex TableSepRx = new Regex(@"^[\s|:\-]+$", RegexOptions.Compiled);

        public static string Convert(string md)
        {
            if (string.IsNullOrEmpty(md)) return "";

            var sb = new StringBuilder();
            var lines = md.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
            var para = new List<string>();
            int i = 0;

            void FlushPara()
            {
                if (para.Count == 0) return;
                sb.Append("<p>").Append(Inline(string.Join(" ", para))).Append("</p>");
                para.Clear();
            }

            while (i < lines.Length)
            {
                var line = lines[i];

                if (line.TrimStart().StartsWith("```"))              // fenced code block
                {
                    FlushPara();
                    var code = new StringBuilder();
                    i++;
                    while (i < lines.Length && !lines[i].TrimStart().StartsWith("```"))
                        code.AppendLine(lines[i++]);
                    i++; // skip closing fence (no-op at EOF when the fence is still open)
                    sb.Append("<pre>").Append(Escape(code.ToString().TrimEnd('\n'))).Append("</pre>");
                    continue;
                }

                var t = line.TrimEnd();
                if (t.Length == 0) { FlushPara(); i++; continue; }

                // table: a "| ... |" row immediately followed by a "|---|---|" rule
                if (t.IndexOf('|') >= 0 && i + 1 < lines.Length && IsTableSeparator(lines[i + 1]))
                {
                    FlushPara();
                    var rows = new List<string[]> { SplitCells(t) };
                    i += 2; // consume header + separator
                    while (i < lines.Length && lines[i].IndexOf('|') >= 0
                           && lines[i].Trim().Length > 0
                           && !lines[i].TrimStart().StartsWith("```"))
                    {
                        rows.Add(SplitCells(lines[i]));
                        i++;
                    }
                    AppendTable(sb, rows);
                    continue;
                }

                var h = HeaderRx.Match(t);                            // header
                if (h.Success)
                {
                    FlushPara();
                    int level = h.Groups[1].Value.Length;
                    sb.Append("<h").Append(level).Append('>')
                      .Append(Inline(h.Groups[2].Value))
                      .Append("</h").Append(level).Append('>');
                    i++; continue;
                }

                var b = BulletRx.Match(t);                            // bullet list
                if (b.Success)
                {
                    FlushPara();
                    sb.Append("<ul>");
                    do
                    {
                        sb.Append("<li>").Append(Inline(b.Groups[1].Value)).Append("</li>");
                        i++;
                        if (i >= lines.Length) break;
                        b = BulletRx.Match(lines[i].TrimEnd());
                    } while (b.Success);
                    sb.Append("</ul>");
                    continue;
                }

                var n = NumberedRx.Match(t);                          // numbered list
                if (n.Success)
                {
                    FlushPara();
                    sb.Append("<ol>");
                    do
                    {
                        sb.Append("<li>").Append(Inline(n.Groups[2].Value)).Append("</li>");
                        i++;
                        if (i >= lines.Length) break;
                        n = NumberedRx.Match(lines[i].TrimEnd());
                    } while (n.Success);
                    sb.Append("</ol>");
                    continue;
                }

                para.Add(t);
                i++;
            }
            FlushPara();
            return sb.ToString();
        }

        /// <summary>Render inline Markdown to HTML, HTML-escaping all literal text.</summary>
        private static string Inline(string text)
        {
            var sb = new StringBuilder();
            int pos = 0;
            foreach (Match m in InlineRx.Matches(text))
            {
                if (m.Index > pos) sb.Append(Escape(text.Substring(pos, m.Index - pos)));

                if (m.Groups["b"].Success)
                    sb.Append("<strong>").Append(Escape(m.Groups["b"].Value)).Append("</strong>");
                else if (m.Groups["i"].Success)
                    sb.Append("<em>").Append(Escape(m.Groups["i"].Value)).Append("</em>");
                else if (m.Groups["c"].Success)
                    sb.Append("<code>").Append(Escape(m.Groups["c"].Value)).Append("</code>");
                else if (m.Groups["t"].Success)
                    sb.Append("<a href=\"").Append(Escape(m.Groups["u"].Value)).Append("\">")
                      .Append(Escape(m.Groups["t"].Value)).Append("</a>");

                pos = m.Index + m.Length;
            }
            if (pos < text.Length) sb.Append(Escape(text.Substring(pos)));
            return sb.ToString();
        }

        private static void AppendTable(StringBuilder sb, List<string[]> rows)
        {
            sb.Append("<table>");
            for (int r = 0; r < rows.Count; r++)
            {
                var tag = r == 0 ? "th" : "td";
                sb.Append("<tr>");
                foreach (var cell in rows[r])
                    sb.Append('<').Append(tag).Append('>').Append(Inline(cell))
                      .Append("</").Append(tag).Append('>');
                sb.Append("</tr>");
            }
            sb.Append("</table>");
        }

        private static bool IsTableSeparator(string s)
            => s.IndexOf('-') >= 0 && TableSepRx.IsMatch(s);

        private static string[] SplitCells(string row)
        {
            var raw = row.Trim().Trim('|').Split('|');
            for (int k = 0; k < raw.Length; k++) raw[k] = raw[k].Trim();
            return raw;
        }

        private static string Escape(string s)
            => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    }
}
