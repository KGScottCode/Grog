// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Guide;

/// <summary>
/// One stop RESOLVED for display: the face chosen, the text to draw, the control to ring, the placement.
/// The view model binds to this and nothing else, so a stop's faces stay a Core concern.
/// </summary>
/// <param name="OnPage">False when this is the "open this page first" face. The renderer does not care,
/// but tests and the view model do: it is the difference between "go there" and "do the thing".</param>
public readonly record struct GuideView(
    string Key,
    string Title,
    string Caption,
    string TargetName,
    string? RequiresView,
    bool OnPage,
    string Detail = "",
    string DetailTitle = "",
    bool Optional = false,
    bool Gate = false,
    string ScopeName = "",
    string AnchorName = "",
    bool NoScrim = false,
    bool Below = false,
    bool Park = false);
