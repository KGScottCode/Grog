// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.App.ViewModels;

/// <summary>(09-22) What a notice's action button does: go to <paramref name="View"/>, and when
/// <paramref name="RetryQueue"/> also start the queue again (the button then reads "Retry", not "Fix").
/// Carried unchanged through the tray's missed-notice replay.</summary>
public sealed record ToastFix(string View, bool RetryQueue = false)
{
    public static readonly ToastFix Retry = new("Overview", RetryQueue: true);
    public static implicit operator ToastFix(string view) => new(view);
}
