using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace ArcGISClaude.UI
{
    /// <summary>
    /// Compact, dependency-free HTML renderer that FOLLOWS Pro's theme. It is a
    /// <see cref="Border"/> whose Child is a stack of TextBlocks/Runs that inherit the
    /// panel's themed foreground, so it reads correctly in dark and light mode — no
    /// embedded browser, no white box.
    ///
    /// It parses the subset the engine is instructed to emit: headings (h1-h6),
    /// paragraphs (p), line breaks (br), bold (b/strong), italics (i/em), inline code
    /// (code), code blocks (pre), lists (ul/ol/li), links (a), and tables
    /// (table/tr/th/td). The tokenizer is deliberately tolerant: malformed or
    /// half-streamed HTML (unclosed tags) degrades gracefully instead of throwing,
    /// because <see cref="Html"/> is re-rendered on every streaming update.
    /// </summary>
    internal sealed class HtmlPresenter : Border
    {
        public static readonly DependencyProperty HtmlProperty =
            DependencyProperty.Register(nameof(Html), typeof(string), typeof(HtmlPresenter),
                new PropertyMetadata(null, (d, e) => ((HtmlPresenter)d).Render((string)e.NewValue)));

        public string Html
        {
            get => (string)GetValue(HtmlProperty);
            set => SetValue(HtmlProperty, value);
        }

        // Inline-level tags accumulate into a TextBlock's Inlines; block-level tags
        // open a new element in the vertical stack.
        private static readonly HashSet<string> BlockTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "h1","h2","h3","h4","h5","h6","p","pre","ul","ol","li","table","thead","tbody","tr","td","th","div" };

        private static readonly Regex TagRx = new Regex(
            @"<(?<close>/?)(?<name>[a-zA-Z][a-zA-Z0-9]*)(?<attrs>[^>]*?)/?>",
            RegexOptions.Compiled);

        private static readonly Regex HrefRx = new Regex(
            @"href\s*=\s*(?:""(?<u>[^""]*)""|'(?<u>[^']*)'|(?<u>[^\s>]+))",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // --------------------------------------------------------------------- //
        //  tokenizer
        // --------------------------------------------------------------------- //
        private sealed class Tok
        {
            public bool IsTag;
            public bool IsClose;
            public string Name;   // lower-cased tag name
            public string Attrs;
            public string Text;   // decoded text (for text tokens)
        }

        private static List<Tok> Tokenize(string html)
        {
            var toks = new List<Tok>();
            int pos = 0;
            foreach (Match m in TagRx.Matches(html))
            {
                if (m.Index > pos)
                    toks.Add(new Tok { Text = WebUtility.HtmlDecode(html.Substring(pos, m.Index - pos)) });
                toks.Add(new Tok
                {
                    IsTag = true,
                    IsClose = m.Groups["close"].Value == "/",
                    Name = m.Groups["name"].Value.ToLowerInvariant(),
                    Attrs = m.Groups["attrs"].Value,
                });
                pos = m.Index + m.Length;
            }
            if (pos < html.Length)
                toks.Add(new Tok { Text = WebUtility.HtmlDecode(html.Substring(pos)) });
            return toks;
        }

        // --------------------------------------------------------------------- //
        //  block-level walk
        // --------------------------------------------------------------------- //
        private void Render(string html)
        {
            var panel = new StackPanel();
            if (!string.IsNullOrEmpty(html))
            {
                var toks = Tokenize(html);
                int i = 0;
                ParseBlocks(toks, ref i, panel, null);
            }
            Child = panel;
        }

        private void ParseBlocks(List<Tok> toks, ref int i, StackPanel panel, string stopTag)
        {
            while (i < toks.Count)
            {
                var tok = toks[i];

                if (tok.IsTag && tok.IsClose)
                {
                    if (stopTag != null && tok.Name == stopTag) { i++; return; }
                    i++; continue; // stray close
                }

                if (tok.IsTag)
                {
                    var name = tok.Name;
                    if (name.Length == 2 && name[0] == 'h' && name[1] >= '1' && name[1] <= '6')
                    {
                        i++;
                        double size = name[1] <= '1' ? 17 : name[1] == '2' ? 15 : 13.5;
                        panel.Children.Add(MakeText(CollectInlines(toks, ref i, name), size, FontWeights.Bold, 0));
                        continue;
                    }
                    switch (name)
                    {
                        case "p":
                        case "div":
                            i++;
                            panel.Children.Add(MakeText(CollectInlines(toks, ref i, name), 0, FontWeights.Normal, 0));
                            continue;
                        case "pre":
                            i++;
                            panel.Children.Add(MakeCodeBlock(CollectText(toks, ref i, "pre").Trim('\n')));
                            continue;
                        case "ul":
                        case "ol":
                            i++;
                            ParseList(toks, ref i, panel, name);
                            continue;
                        case "table":
                            i++;
                            ParseTable(toks, ref i, panel);
                            continue;
                        case "br":
                            i++; continue; // a bare block-level break is just a gap
                        default:
                            // An inline tag (b/i/code/a) opening at block level → treat the
                            // run that follows as a loose paragraph.
                            panel.Children.Add(MakeText(CollectInlines(toks, ref i, stopTag), 0, FontWeights.Normal, 0));
                            continue;
                    }
                }

                // bare text at block level
                if (string.IsNullOrWhiteSpace(tok.Text)) { i++; continue; }
                panel.Children.Add(MakeText(CollectInlines(toks, ref i, stopTag), 0, FontWeights.Normal, 0));
            }
        }

        // --------------------------------------------------------------------- //
        //  inline-level walk
        // --------------------------------------------------------------------- //
        private List<Inline> CollectInlines(List<Tok> toks, ref int i, string stopTag)
        {
            var result = new List<Inline>();
            while (i < toks.Count)
            {
                var tok = toks[i];

                if (tok.IsTag && tok.IsClose)
                {
                    if (stopTag != null && tok.Name == stopTag) { i++; break; }
                    i++; continue; // stray close
                }

                if (tok.IsTag)
                {
                    // A block-level open tag ends this inline run — leave it for the
                    // block parser (don't consume it).
                    if (BlockTags.Contains(tok.Name)) break;

                    var name = tok.Name;
                    i++;
                    switch (name)
                    {
                        case "b":
                        case "strong":
                            AddSpan(result, new Bold(), CollectInlines(toks, ref i, name));
                            break;
                        case "i":
                        case "em":
                            AddSpan(result, new Italic(), CollectInlines(toks, ref i, name));
                            break;
                        case "code":
                            var code = new Run(CollectText(toks, ref i, "code")) { FontFamily = new FontFamily("Consolas") };
                            code.SetResourceReference(TextElement.BackgroundProperty, "Esri_BackgroundPressedBrush");
                            result.Add(code);
                            break;
                        case "a":
                            var url = MatchHref(tok.Attrs);
                            var link = new Hyperlink();
                            foreach (var inl in CollectInlines(toks, ref i, "a")) link.Inlines.Add(inl);
                            if (!string.IsNullOrEmpty(url))
                            {
                                try { link.NavigateUri = new Uri(url, UriKind.RelativeOrAbsolute); } catch { }
                                link.RequestNavigate += (s, e) =>
                                {
                                    try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
                                };
                            }
                            result.Add(link);
                            break;
                        case "br":
                            result.Add(new LineBreak());
                            break;
                        default:
                            break; // unknown inline tag: drop the tag, keep its text
                    }
                    continue;
                }

                result.Add(new Run(tok.Text));
                i++;
            }
            return result;
        }

        /// <summary>Collect decoded text only (tags ignored) until the matching close — for code/pre.</summary>
        private static string CollectText(List<Tok> toks, ref int i, string stopTag)
        {
            var sb = new StringBuilder();
            while (i < toks.Count)
            {
                var tok = toks[i];
                if (tok.IsTag && tok.IsClose && tok.Name == stopTag) { i++; break; }
                if (!tok.IsTag) sb.Append(tok.Text);
                i++;
            }
            return sb.ToString();
        }

        private static void AddSpan(List<Inline> result, Span span, List<Inline> children)
        {
            foreach (var c in children) span.Inlines.Add(c);
            result.Add(span);
        }

        private static string MatchHref(string attrs)
        {
            if (string.IsNullOrEmpty(attrs)) return null;
            var m = HrefRx.Match(attrs);
            return m.Success ? m.Groups["u"].Value : null;
        }

        // --------------------------------------------------------------------- //
        //  lists & tables
        // --------------------------------------------------------------------- //
        private void ParseList(List<Tok> toks, ref int i, StackPanel panel, string listName)
        {
            bool ordered = listName == "ol";
            int n = 1;
            while (i < toks.Count)
            {
                var tok = toks[i];
                if (tok.IsTag && tok.IsClose)
                {
                    if (tok.Name == listName) { i++; return; }
                    i++; continue;
                }
                if (tok.IsTag && tok.Name == "li")
                {
                    i++;
                    var prefix = ordered ? (n++) + ".  " : "•  ";
                    panel.Children.Add(MakeText(CollectInlines(toks, ref i, "li"), 0, FontWeights.Normal, 14, prefix));
                    continue;
                }
                if (tok.IsTag && BlockTags.Contains(tok.Name)) return; // another block → list ended
                i++; // whitespace / stray inline between items
            }
        }

        private void ParseTable(List<Tok> toks, ref int i, StackPanel panel)
        {
            var rows = new List<List<List<Inline>>>();
            while (i < toks.Count)
            {
                var tok = toks[i];
                if (tok.IsTag && tok.IsClose)
                {
                    if (tok.Name == "table") { i++; break; }
                    i++; continue;
                }
                if (tok.IsTag && tok.Name == "tr")
                {
                    i++;
                    rows.Add(ParseRow(toks, ref i));
                    continue;
                }
                if (tok.IsTag && (tok.Name == "thead" || tok.Name == "tbody")) { i++; continue; }
                if (tok.IsTag && BlockTags.Contains(tok.Name) && tok.Name != "td" && tok.Name != "th") break;
                i++; // whitespace between rows
            }
            if (rows.Count > 0) panel.Children.Add(MakeTable(rows));
        }

        private List<List<Inline>> ParseRow(List<Tok> toks, ref int i)
        {
            var cells = new List<List<Inline>>();
            while (i < toks.Count)
            {
                var tok = toks[i];
                if (tok.IsTag && tok.IsClose)
                {
                    if (tok.Name == "tr") { i++; break; }
                    if (tok.Name == "table") break; // table close: let ParseTable consume it
                    i++; continue;
                }
                if (tok.IsTag && (tok.Name == "td" || tok.Name == "th"))
                {
                    var name = tok.Name;
                    i++;
                    cells.Add(CollectInlines(toks, ref i, name));
                    continue;
                }
                if (tok.IsTag && tok.Name == "tr") break;        // new row without a close
                if (tok.IsTag && BlockTags.Contains(tok.Name)) break;
                i++; // whitespace between cells
            }
            return cells;
        }

        // --------------------------------------------------------------------- //
        //  themed element builders
        // --------------------------------------------------------------------- //
        private TextBlock MakeText(List<Inline> inlines, double fontSize, FontWeight weight, double indent, string prefix = null)
        {
            var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(indent, 2, 0, 2) };
            if (fontSize > 0) tb.FontSize = fontSize;
            if (weight != FontWeights.Normal) tb.FontWeight = weight;
            if (!string.IsNullOrEmpty(prefix)) tb.Inlines.Add(new Run(prefix));
            foreach (var inl in inlines) tb.Inlines.Add(inl);
            return tb;
        }

        private FrameworkElement MakeTable(List<List<List<Inline>>> rows)
        {
            int cols = 0;
            foreach (var r in rows) cols = Math.Max(cols, r.Count);

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
                    var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(5, 3, 5, 3) };
                    if (r == 0) tb.FontWeight = FontWeights.Bold;        // header row
                    if (c < cells.Count) foreach (var inl in cells[c]) tb.Inlines.Add(inl);

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
