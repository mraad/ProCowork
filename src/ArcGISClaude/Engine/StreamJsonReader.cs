using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace ArcGISClaude.Engine
{
    /// <summary>
    /// Helpers for interpreting Claude Code's <c>--output-format stream-json</c>
    /// events (one JSON object per stdout line). Kept separate from the process
    /// plumbing so the view model can stay readable.
    ///
    /// Event <c>type</c> values handled by the UI:
    ///   system/init  -> session id, model, mcp servers, apiKeySource
    ///   assistant    -> message.content blocks: text | tool_use | thinking
    ///   user         -> message.content blocks: tool_result
    ///   result       -> final turn text + usage + is_error
    /// </summary>
    internal static class StreamJsonReader
    {
        public static string Type(JObject ev) => (string)ev["type"];
        public static string Subtype(JObject ev) => (string)ev["subtype"];

        /// <summary>The message's content blocks of one <c>type</c> ("text", "tool_use", …).</summary>
        private static IEnumerable<JObject> Blocks(JObject ev, string type)
            => (ev["message"]?["content"] as JArray ?? new JArray())
                .OfType<JObject>().Where(b => (string)b["type"] == type);

        public static IEnumerable<JObject> ToolUseBlocks(JObject ev) => Blocks(ev, "tool_use");

        public static IEnumerable<JObject> ToolResultBlocks(JObject ev) => Blocks(ev, "tool_result");

        public static string JoinedText(JObject ev)
            => string.Join("", Blocks(ev, "text").Select(b => (string)b["text"]));

        /// <summary>tool_result content may be a string or an array of text parts.</summary>
        public static string ToolResultText(JObject block)
        {
            var content = block["content"];
            if (content == null) return "";
            if (content.Type == JTokenType.String) return (string)content;
            if (content is JArray arr)
                return string.Join("\n", arr.OfType<JObject>()
                    .Where(p => (string)p["type"] == "text")
                    .Select(p => (string)p["text"]));
            return content.ToString();
        }

        /// <summary>
        /// Returns the human-readable payload of a tool_use input: the generated
        /// code or script path when the input carries one, else indented JSON.
        /// Driven by the input shape (the <c>code</c>/<c>path</c> fields), so it
        /// needs no per-tool-name special-casing.
        /// </summary>
        public static string DescribeToolInput(JToken input)
        {
            if (input == null) return "";
            return (string)input["code"]
                   ?? (string)input["path"]
                   ?? input.ToString(Newtonsoft.Json.Formatting.Indented);
        }

        /// <summary>Short, friendly tool name (strips the mcp__server__ prefix).</summary>
        public static string ShortToolName(string toolName)
        {
            if (string.IsNullOrEmpty(toolName)) return toolName;
            var idx = toolName.LastIndexOf("__", System.StringComparison.Ordinal);
            return idx >= 0 ? toolName.Substring(idx + 2) : toolName;
        }
    }
}
