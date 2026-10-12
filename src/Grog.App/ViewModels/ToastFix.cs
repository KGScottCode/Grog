// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.App.ViewModels;

public sealed record ToastFix(string View, bool RetryQueue = false)
{
    public static readonly ToastFix Retry = new("Overview", RetryQueue: true);
    public static implicit operator ToastFix(string view) => new(view);
}
