using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ArcGISClaude.UI
{
    /// <summary>Minimal INotifyPropertyChanged base for the chat view models.</summary>
    internal abstract class ObservableObject : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected void Raise([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        protected bool Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (Equals(field, value)) return false;
            field = value;
            Raise(name);
            return true;
        }
    }

    /// <summary>Base type for everything shown in the chat transcript.</summary>
    internal abstract class ChatItemVm : ObservableObject { }

    /// <summary>A message the user typed.</summary>
    internal sealed class UserMessageVm : ChatItemVm
    {
        public string Text { get; }
        public UserMessageVm(string text) => Text = text;
    }

    /// <summary>Assistant prose. Holds raw Markdown from the engine; the panel renders
    /// <see cref="Html"/> (Markdown converted to HTML) via HtmlPresenter.</summary>
    internal sealed class AssistantMessageVm : ChatItemVm
    {
        private string _text;
        private string _html;
        public string Text
        {
            get => _text;
            set { if (Set(ref _text, value)) { _html = null; Raise(nameof(Html)); } }
        }

        // Converted lazily and cached: WPF reads Html more than once per bind
        // (measure/arrange), and the Markdown→HTML pass is pure for a fixed Text.
        public string Html => _html ??= MarkdownToHtml.Convert(_text);
    }

    /// <summary>System/connection notices and surfaced errors.</summary>
    internal sealed class SystemNoticeVm : ChatItemVm
    {
        public string Text { get; }
        public SystemNoticeVm(string text) => Text = text;
    }
}
