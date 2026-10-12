// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Guide;

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
