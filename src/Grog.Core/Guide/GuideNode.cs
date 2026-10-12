// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;

namespace Grog.Core.Guide;

public readonly record struct GuideNode(
    string Key,
    string Title,
    int Number,
    string Caption,
    string TargetName,
    string? RequiresView = null,
    string BusyCaption = "",
    string DoneCaption = "",
    string DoneTarget = "",
    string AltCaption = "",
    string AltTarget = "",
    string Detail = "",
    string DetailTitle = "",
    bool Optional = false,
    bool Gate = false,
    bool SkipOnBack = false,
    string ScopeName = "",
    string AnchorName = "",
    bool NoScrim = false,
    bool Below = false,
    bool Park = false)
{
    public Func<GuideState, bool>? IsDone { get; init; }

    public Func<GuideState, bool>? IsBusy { get; init; }

    public Func<GuideState, bool>? IsLanded { get; init; }

    public Func<GuideState, bool>? IsAlt { get; init; }

    public Func<GuideState, bool>? Relevant { get; init; }

    public Func<GuideState, string?>? DestinationFor { get; init; }
}
