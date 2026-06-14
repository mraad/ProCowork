namespace ArcGISClaude.UI
{
    /// <summary>
    /// One tool invocation in the transcript. For the code-gen tools,
    /// <see cref="Code"/> holds the actual Python/ArcPy that ran on the live
    /// project (shown when present). <see cref="Result"/> is filled in when the
    /// matching tool_result arrives.
    /// </summary>
    internal sealed class ToolCallVm : ChatItemVm
    {
        public string ToolName { get; }

        private string _code;
        public string Code
        {
            get => _code;
            set { if (Set(ref _code, value)) Raise(nameof(HasCode)); }
        }
        public bool HasCode => !string.IsNullOrEmpty(_code);

        private string _status = "running…";
        public string Status { get => _status; set => Set(ref _status, value); }

        private string _result;
        public string Result
        {
            get => _result;
            set { if (Set(ref _result, value)) Raise(nameof(HasResult)); }
        }
        public bool HasResult => !string.IsNullOrEmpty(_result);

        private bool _isError;
        public bool IsError { get => _isError; set => Set(ref _isError, value); }

        public ToolCallVm(string fullName, string code)
        {
            ToolName = Engine.StreamJsonReader.ShortToolName(fullName);
            _code = code;
        }
    }
}
