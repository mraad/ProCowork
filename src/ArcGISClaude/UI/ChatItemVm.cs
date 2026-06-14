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

    /// <summary>Assistant prose.</summary>
    internal sealed class AssistantMessageVm : ChatItemVm
    {
        private string _text;
        public string Text { get => _text; set => Set(ref _text, value); }
    }

    /// <summary>System/connection notices and surfaced errors.</summary>
    internal sealed class SystemNoticeVm : ChatItemVm
    {
        public string Text { get; }
        public bool IsError { get; }
        public SystemNoticeVm(string text, bool isError = false)
        {
            Text = text;
            IsError = isError;
        }
    }
}
