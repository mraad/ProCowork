using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace ArcGISClaude.UI
{
    /// <summary>
    /// Compact, dependency-free Markdown renderer that FOLLOWS Pro's theme. It is a
    /// <see cref="Border"/> whose Child is a stack of TextBlocks/Runs that inherit the
    /// panel's themed foreground, so it reads correctly in dark and light mode.
    /// Handles headers, bold, italic, inline code, fenced code blocks, bullet/numbered
    /// lists, links, and paragraphs.
    ///
    /// Note: emphasis is only '*'/'**' (not '_'), so snake_case identifiers such as
    /// run_python_current and arcpy.da are never mangled into italics.
    /// </summary>
    internal sealed class MarkdownPresenter : Border
    {
        public static readonly DependencyProperty MarkdownProperty =
            DependencyProperty.Register(nameof(Markdown), typeof(string), typeof(MarkdownPresenter),
                new PropertyMetadata(null, (d, e) => ((MarkdownPresenter)d).Render((string)e.NewValue)));

        public string Markdown
        {
            get => (string)GetValue(MarkdownProperty);
            set => SetValue(MarkdownProperty, value);
        }

        private static readonly Regex InlineRx = new Regex(
            @"\*\*(?<b>[^*]+?)\*\*|\*(?<i>[^*]+?)\*|`(?<c>[^`]+?)`|\[(?<t>[^\]]+?)\]\((?<u>[^)]+?)\)",
            RegexOptions.Compiled);

        private void Render(string md)
        {
            var panel = new StackPanel();
            if (!string.IsNullOrEmpty(md))
            {
                var lines = md.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
                var para = new List<string>();
                int i = 0;

                void FlushPara()
                {
                    if (para.Count == 0) return;
                    panel.Children.Add(MakeText(string.Join(" ", para), 0, FontWeights.Normal, 0));
                    para.Clear();
                }

                while (i < lines.Length)
                {
                    var line = lines[i];

                    if (line.TrimStart().StartsWith("```"))            // fenced code block
                    {
                        FlushPara();
                        var code = new StringBuilder();
                        i++;
                        while (i < lines.Length && !lines[i].TrimStart().StartsWith("```"))
                            code.AppendLine(lines[i++]);
                        i++; // skip closing fence
                        panel.Children.Add(MakeCodeBlock(code.ToString().TrimEnd('\n')));
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
                        panel.Children.Add(MakeTable(rows));
                        continue;
                    }

                    var h = Regex.Match(t, @"^(#{1,6})\s+(.*)$");      // header
                    if (h.Success)
                    {
                        FlushPara();
                        int level = h.Groups[1].Value.Length;
                        double size = level <= 1 ? 17 : level == 2 ? 15 : 13.5;
                        panel.Children.Add(MakeText(h.Groups[2].Value, size, FontWeights.Bold, 0));
                        i++; continue;
                    }

                    var b = Regex.Match(t, @"^\s*[-*+]\s+(.*)$");       // bullet item
                    if (b.Success)
                    {
                        FlushPara();
                        panel.Children.Add(MakeText(b.Groups[1].Value, 0, FontWeights.Normal, 14, "•  "));
                        i++; continue;
                    }

                    var n = Regex.Match(t, @"^\s*(\d+)\.\s+(.*)$");     // numbered item
                    if (n.Success)
                    {
                        FlushPara();
                        panel.Children.Add(MakeText(n.Groups[2].Value, 0, FontWeights.Normal, 14, n.Groups[1].Value + ".  "));
                        i++; continue;
                    }

                    para.Add(t);
                    i++;
                }
                FlushPara();
            }
            Child = panel;
        }

        private TextBlock MakeText(string text, double fontSize, FontWeight weight, double indent, string prefix = null)
        {
            var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(indent, 2, 0, 2) };
            if (fontSize > 0) tb.FontSize = fontSize;
            if (weight != FontWeights.Normal) tb.FontWeight = weight;
            if (!string.IsNullOrEmpty(prefix)) tb.Inlines.Add(new Run(prefix));
            foreach (var inl in ParseInlines(text)) tb.Inlines.Add(inl);
            return tb;
        }

        private IEnumerable<Inline> ParseInlines(string text)
        {
            var result = new List<Inline>();
            int pos = 0;
            foreach (Match m in InlineRx.Matches(text))
            {
                if (m.Index > pos) result.Add(new Run(text.Substring(pos, m.Index - pos)));

                if (m.Groups["b"].Success)
                    result.Add(new Bold(new Run(m.Groups["b"].Value)));
                else if (m.Groups["i"].Success)
                    result.Add(new Italic(new Run(m.Groups["i"].Value)));
                else if (m.Groups["c"].Success)
                {
                    var code = new Run(m.Groups["c"].Value) { FontFamily = new FontFamily("Consolas") };
                    code.SetResourceReference(TextElement.BackgroundProperty, "Esri_BackgroundPressedBrush");
                    result.Add(code);
                }
                else if (m.Groups["t"].Success)
                {
                    var url = m.Groups["u"].Value;
                    var link = new Hyperlink(new Run(m.Groups["t"].Value));
                    try { link.NavigateUri = new Uri(url, UriKind.RelativeOrAbsolute); } catch { }
                    link.RequestNavigate += (s, e) =>
                    {
                        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
                    };
                    result.Add(link);
                }
                pos = m.Index + m.Length;
            }
            if (pos < text.Length) result.Add(new Run(text.Substring(pos)));
            return result;
        }

        private static bool IsTableSeparator(string s)
            => s.IndexOf('-') >= 0 && Regex.IsMatch(s, @"^[\s|:\-]+$");

        private static string[] SplitCells(string row)
        {
            var raw = row.Trim().Trim('|').Split('|');
            for (int k = 0; k < raw.Length; k++) raw[k] = raw[k].Trim();
            return raw;
        }

        private FrameworkElement MakeTable(List<string[]> rows)
        {
            int cols = 0;
            foreach (var r in rows) cols = Math.Max(cols, r.Length);

            var grid = new Grid();
            for (int c = 0; c < cols; c++)
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int r = 0; r < rows.Count; r++)
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            for (int r = 0; r < rows.Count; r++)
            {
                var cells = rows[r];
                for (int c = 0; c < cols; c++)
                {
                    var text = c < cells.Length ? cells[c] : "";
                    var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(5, 3, 5, 3) };
                    if (r == 0) tb.FontWeight = FontWeights.Bold;        // header row
                    foreach (var inl in ParseInlines(text)) tb.Inlines.Add(inl);

                    var cell = new Border { Child = tb, BorderThickness = new Thickness(0, 0, 1, 1) };
                    cell.SetResourceReference(Border.BorderBrushProperty, "Esri_BorderBrush");
                    Grid.SetRow(cell, r);
                    Grid.SetColumn(cell, c);
                    grid.Children.Add(cell);
                }
            }

            var outer = new Border
            {
                Child = grid,
                BorderThickness = new Thickness(1, 1, 0, 0), // top + left; cells draw right + bottom
                Margin = new Thickness(0, 4, 0, 4),
            };
            outer.SetResourceReference(Border.BorderBrushProperty, "Esri_BorderBrush");
            return outer;
        }

        private FrameworkElement MakeCodeBlock(string code)
        {
            var tb = new TextBlock
            {
                Text = code,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11,
                TextWrapping = TextWrapping.NoWrap,
            };
            var scroller = new ScrollViewer
            {
                Content = tb,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MaxHeight = 260,
            };
            var border = new Border
            {
                Child = scroller,
                Padding = new Thickness(6),
                Margin = new Thickness(0, 3, 0, 3),
                CornerRadius = new CornerRadius(3),
            };
            border.SetResourceReference(Border.BackgroundProperty, "Esri_BackgroundPressedBrush");
            return border;
        }
    }
}
