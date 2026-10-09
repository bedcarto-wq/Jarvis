using Jarvis.Core.Apps;

namespace Jarvis.Core.Commands;

public enum CommandIntent
{
    Empty, Unknown, Ambiguous,
    OpenApp, SwitchTo, CloseApp, CloseActive, MinimizeActive, MinimizeApp, MaximizeActive, MaximizeApp, ShowDesktop,
    OpenSite, NewTab, NextTab, PrevTab, CloseTab,
    VolumeUp, VolumeDown, Mute, Unmute, SetVolume,
    Screenshot, Copy, Paste, SelectAll, TypeText, Undo,
    Remember, PointHere,
    Disable, Enable, Stop, Pause, Resume, Confirm, Deny, Help,
    Benchmark,
    RunTemplate, CreateTemplate, NameTemplate, AddStep, SaveTemplate, CancelTemplate,
}

public sealed record ParsedCommand(
    CommandIntent Intent,
    string Normalized,
    string? Target = null,
    int? Number = null,
    AppMatchResult? App = null,
    string? Url = null,
    string? TemplateId = null,
    string? Message = null)
{
    public override string ToString() => $"{Intent}{(Target is null ? "" : $" «{Target}»")}";
}
