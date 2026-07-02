using System;
using System.Text;
using Markdig;
using Markdig.Extensions.EmphasisExtras;

namespace ArcGISClaude.UI
{
    /// <summary>
    /// Converts the engine's Markdown prose into the HTML that
    /// <see cref="HtmlPresenter"/> renders. The engine keeps emitting Markdown —
    /// its natural, higher-quality format — and the panel converts here so the
    /// output is rendered as HTML.
    ///
    /// CommonMark's intraword rule for '_' already keeps snake_case identifiers
    /// such as run_python_current and arcpy.da from being mangled into italics,
    /// so no special-casing is needed here. Extensions are chosen deliberately
    /// (not UseAdvancedExtensions()) so Markdig never emits tags HtmlPresenter
    /// can't render (footnotes, definition lists, task-list checkboxes, custom
    /// containers).
    /// </summary>
    internal static class MarkdownToHtml
    {
        private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
            .UsePipeTables()
            .UseAutoLinks()
            .UseEmphasisExtras(EmphasisExtraOptions.Strikethrough)
            .Build();

        public static string Convert(string md)
        {
            if (string.IsNullOrEmpty(md)) return "";
            return Markdown.ToHtml(UnwrapMarkdownFences(md), Pipeline);
        }

        /// <summary>
        /// The engine sometimes wraps its prose answer — tables especially — in a
        /// ```markdown fence. Left alone, Markdig turns that into a code block and
        /// the panel shows literal pipe characters. Splice the contents of
        /// ```markdown / ```md fences back in as normal Markdown; real code fences
        /// (```python etc.) pass through untouched. Tolerant of an unclosed fence
        /// because the text is re-converted on every streaming update.
        /// </summary>
        private static string UnwrapMarkdownFences(string md)
        {
            if (md.IndexOf("```", StringComparison.Ordinal) < 0) return md;

            var sb = new StringBuilder(md.Length);
            bool inMdFence = false, inOtherFence = false;
            foreach (var line in md.Split('\n'))
            {
                var t = line.TrimStart();
                if (t.StartsWith("```", StringComparison.Ordinal))
                {
                    if (inMdFence) { inMdFence = false; continue; }          // drop the closer
                    if (!inOtherFence)
                    {
                        var info = t.Substring(3).Trim();
                        if (info.Equals("markdown", StringComparison.OrdinalIgnoreCase) ||
                            info.Equals("md", StringComparison.OrdinalIgnoreCase))
                        {
                            inMdFence = true; continue;                      // drop the opener
                        }
                        inOtherFence = true;                                 // a real code fence opens
                    }
                    else
                        inOtherFence = false;                                // a real code fence closes
                }
                sb.Append(line).Append('\n');
            }
            return sb.ToString();
        }
    }
}
