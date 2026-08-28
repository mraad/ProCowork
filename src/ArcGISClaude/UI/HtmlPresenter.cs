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
    /// It parses the HTML subset Markdig emits from the engine's Markdown: headings
    /// (h1-h6), paragraphs (p), line breaks (br/hr), bold (b/strong), italics
    /// (i/em), strikethrough (del/s), inline code (code), code blocks (pre), lists
    /// (ul/ol/li, including nesting), blockquotes, links (a), and tables
    /// (table/tr/th/td). The tokenizer is deliberately tolerant: malformed or
    /// half-streamed HTML (unclosed tags) degrades gracefully instead of throwing.
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
        { "h1","h2","h3","h4","h5","h6","p","pre","ul","ol","li","table","thead","tbody","tr","td","th","div","blockquote","hr" };

        private static readonly Regex TagRx = new Regex(
            @"<(?<close>/?)(?<name>[a-zA-Z][a-zA-Z0-9]*)(?<attrs>[^>]*?)/?>",
            RegexOptions.Compiled);

        private static readonly Regex HrefRx = new Regex(
            @"href\s*=\s*(?:""(?<u>[^""]*)""|'(?<u>[^']*)'|(?<u>[^\s>]+))",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly FontFamily CodeFont = new FontFamily("Consolas");

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
                        case "blockquote":
                            i++;
                            panel.Children.Add(MakeBlockquote(toks, ref i));
                            continue;
                        case "hr":
                            i++;
                            panel.Children.Add(MakeHr());
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
                    // Markdig wraps loose list-item text in <p>; skip the tag, keep the text.
                    if (stopTag == "li" && tok.Name == "p") { i++; continue; }

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
                        case "del":
                        case "s":
                            AddSpan(result, new Span { TextDecorations = TextDecorations.Strikethrough }, CollectInlines(toks, ref i, name));
                            break;
                        case "code":
                            var code = new Run(CollectText(toks, ref i, "code")) { FontFamily = CodeFont };
                            code.SetResourceReference(TextElement.BackgroundProperty, "Esri_BackgroundPressedBrush");
                            result.Add(code);
                            break;
                        case "a":
                            var url = MatchHref(tok.Attrs);
                            var link = new Hyperlink();
                            foreach (var inl in CollectInlines(toks, ref i, "a")) link.Inlines.Add(inl);
                            if (TryCreateSafeUri(url, out var uri))
                            {
                                link.NavigateUri = uri;
                                link.RequestNavigate += (s, e) =>
                                {
                                    try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); } catch { }
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

        private static bool TryCreateSafeUri(string url, out Uri uri)
        {
            uri = null;
            if (string.IsNullOrWhiteSpace(url)) return false;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var candidate)) return false;
            if (candidate.Scheme != Uri.UriSchemeHttp &&
                candidate.Scheme != Uri.UriSchemeHttps &&
                candidate.Scheme != Uri.UriSchemeMailto)
                return false;
            uri = candidate;
            return true;
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
                    panel.Children.Add(ParseListItem(toks, ref i, prefix));
                    continue;
                }
                if (tok.IsTag && BlockTags.Contains(tok.Name)) return; // another block → list ended
                i++; // whitespace / stray inline between items
            }
        }

        /// <summary>
        /// Parse one &lt;li&gt;'s body: an inline text line, plus an optional nested
        /// &lt;ul&gt;/&lt;ol&gt; rendered indented beneath it.
        /// </summary>
        private FrameworkElement ParseListItem(List<Tok> toks, ref int i, string prefix)
        {
            var itemPanel = new StackPanel();
            while (true)
            {
                var inlines = CollectInlines(toks, ref i, "li");
                if (inlines.Count > 0 || prefix != null)
                {
                    itemPanel.Children.Add(MakeText(inlines, 0, FontWeights.Normal, 14, prefix));
                    prefix = null;
                }

                if (i < toks.Count && toks[i].IsTag && !toks[i].IsClose &&
                    (toks[i].Name == "ul" || toks[i].Name == "ol"))
                {
                    var nestedName = toks[i].Name;
                    i++;
                    var nestedPanel = new StackPanel { Margin = new Thickness(18, 0, 0, 0) };
                    ParseList(toks, ref i, nestedPanel, nestedName);
                    itemPanel.Children.Add(nestedPanel);
                    continue; // there may be more content after the nested list
                }

                break; // </li> was consumed by CollectInlines (or we hit EOF)
            }
            return itemPanel;
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

            // Min width per column = longest word (plus cell padding) so headers
            // wrap on spaces instead of stretching to the full phrase, while
            // unbreakable tokens (paths, SHAPE@XY) still force the table wider
            // than the panel — the ScrollViewer below then shows an H-bar.
            var colMin = new double[cols];
            for (int c = 0; c < cols; c++) colMin[c] = 32;
            for (int r = 0; r < rows.Count; r++)
            {
                var cells = rows[r];
                var weight = r == 0 ? FontWeights.Bold : FontWeights.Normal;
                for (int c = 0; c < cells.Count; c++)
                    colMin[c] = Math.Max(colMin[c], MinWordWidth(cells[c], weight));
            }

            double minTotal = 0;
            var grid = new Grid();
            for (int c = 0; c < cols; c++)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = new GridLength(1, GridUnitType.Star),
                    MinWidth = colMin[c],
                });
                minTotal += colMin[c];
            }
            grid.MinWidth = minTotal;
            // Start bounded so the first (infinite-width) measure inside the
            // ScrollViewer doesn't lay every cell out unwrapped; the SizeChanged
            // handler below raises it to the scroller width on the first pass.
            grid.Width = minTotal;

            for (int r = 0; r < rows.Count; r++)
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            for (int r = 0; r < rows.Count; r++)
            {
                var cells = rows[r];
                for (int c = 0; c < cols; c++)
                {
                    var tb = new TextBlock
                    {
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(5, 3, 5, 3),
                    };
                    if (r == 0) tb.FontWeight = FontWeights.Bold;        // header row
                    if (c < cells.Count) foreach (var inl in cells[c]) tb.Inlines.Add(inl);

                    var cell = new Border { Child = tb, BorderThickness = new Thickness(0, 0, 1, 1) };
                    cell.SetResourceReference(Border.BorderBrushProperty, "Esri_BorderBrush");
                    if (r == 0) cell.SetResourceReference(Border.BackgroundProperty, "Esri_BackgroundPressedBrush");
                    Grid.SetRow(cell, r);
                    Grid.SetColumn(cell, c);
                    grid.Children.Add(cell);
                }
            }

            var outer = new Border
            {
                Child = grid,
                BorderThickness = new Thickness(1, 1, 0, 0), // top + left; cells draw right + bottom
            };
            outer.SetResourceReference(Border.BorderBrushProperty, "Esri_BorderBrush");

            var scroller = new ScrollViewer
            {
                Content = outer,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Margin = new Thickness(0, 4, 0, 4),
            };
            // ScrollViewer measures its child at infinite width, so Star columns
            // would never wrap. Pin the grid to the viewport (or the word-min
            // total if larger) so columns fill the table when they fit, and an
            // H-bar appears only when unbreakable content overflows. `outer`'s
            // border sits between grid and viewport, so its thickness comes off
            // the top: pin to the full width and the extent is always wider than
            // the viewport, which shows an H-bar under every table. SizeChanged
            // fires on the first layout pass (0 -> N), so no Loaded hook needed.
            double chrome = outer.BorderThickness.Left + outer.BorderThickness.Right;
            scroller.SizeChanged += (s, e) =>
            {
                double avail = scroller.ViewportWidth - chrome;
                if (avail > 0) grid.Width = Math.Max(avail, minTotal);
            };
            return scroller;
        }

        /// <summary>Widest whitespace-delimited token in a cell, plus horizontal padding.</summary>
        private static double MinWordWidth(List<Inline> inlines, FontWeight weight)
        {
            const double pad = 10; // TextBlock margin 5+5
            var text = PlainText(inlines);
            if (string.IsNullOrWhiteSpace(text)) return 24 + pad;
            double max = 0;
            foreach (var word in text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries))
            {
                var tb = new TextBlock { Text = word, FontWeight = weight };
                tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                if (tb.DesiredSize.Width > max) max = tb.DesiredSize.Width;
            }
            return max + pad;
        }

        private static string PlainText(IEnumerable<Inline> inlines)
        {
            var sb = new StringBuilder();
            AppendPlain(inlines, sb);
            return sb.ToString();
        }

        private static void AppendPlain(IEnumerable<Inline> inlines, StringBuilder sb)
        {
            if (inlines == null) return;
            foreach (var inl in inlines)
            {
                if (inl is Run r) sb.Append(r.Text);
                else if (inl is LineBreak) sb.Append(' ');
                else if (inl is Span s) AppendPlain(s.Inlines, sb);
            }
        }

        private FrameworkElement MakeBlockquote(List<Tok> toks, ref int i)
        {
            var inner = new StackPanel();
            ParseBlocks(toks, ref i, inner, "blockquote");
            var border = new Border
            {
                Child = inner,
                BorderThickness = new Thickness(3, 0, 0, 0),
                Padding = new Thickness(10, 2, 0, 2),
                Margin = new Thickness(0, 3, 0, 3),
            };
            border.SetResourceReference(Border.BorderBrushProperty, "Esri_BorderBrush");
            return border;
        }

        private FrameworkElement MakeHr()
        {
            var rule = new Border { Height = 1, Margin = new Thickness(0, 8, 0, 8) };
            rule.SetResourceReference(Border.BackgroundProperty, "Esri_BorderBrush");
            return rule;
        }

        private FrameworkElement MakeCodeBlock(string code)
        {
            var tb = new TextBlock
            {
                Text = code,
                FontFamily = CodeFont,
                FontSize = 11,
                TextWrapping = TextWrapping.NoWrap,
            };
            var scroller = new ScrollViewer
            {
                Content = tb,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
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
