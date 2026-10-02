// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using Grog.Core.Auth;

namespace Grog.App.Services;

/// <summary>In-app GOG login: opens GOG's real login page in a native WebView (WebAuthenticationBroker) and
/// captures the authorization code on the on_login_success redirect.</summary>
/// <remarks>Credentials are entered on GOG's genuine page and never pass through Grog; Grog only sees the
/// one-time auth code the engine exchanges for tokens.</remarks>
public sealed class WebViewLoginProvider : IInteractiveLoginProvider
{
    // EVERY login runs in a throwaway session -- no persistent WebView profile, ever. The profile existed
    // to skip the WebView cold start on later logins, but its cookies pre-filled identities as
    // non-editable: GOG's (a marker-file workaround used to force freshness after logout) and, worse,
    // the SSO providers' -- a remembered Google session auto-selected the PREVIOUS account with no way
    // to pick another (owner decision 2026-08-24). Sign-in is rare (the refresh token keeps sessions
    // alive for months), so a cold start per login costs nothing worth this class of bug.

    // The live login dialog, so the connect scrim's Cancel can close it from outside. Closing makes the
    // broker return null, which flows into the existing "Login was closed" cancellation path -- one exit,
    // not a second cancellation mechanism. Static: at most one interactive login exists at a time (the
    // scrim covers the whole window). Exists because a minimized-then-restored app could lose the native
    // dialog with no way out but killing the process (Linux walk 2026-08-23, N2).
    private static NativeWebDialog? _activeDialog;

    /// <summary>Close the in-flight login dialog, if any. Safe from any thread; returns false when no
    /// login is active.</summary>
    public static bool TryCancelActiveLogin()
    {
        var dlg = _activeDialog;
        if (dlg is null) return false;
        Dispatcher.UIThread.Post(() => { try { dlg.Close(); } catch { /* already gone */ } });
        return true;
    }

    private readonly Func<TopLevel?> _topLevel;

    // Size the native window to GOG's login-card footprint plus chrome, so the dialog is not a big
    // black rectangle while the WebView cold-starts.
    private const int LoginWidth = 480;
    private const int LoginHeight = 720;

    /// <param name="topLevel">Resolves the owner window at call time (the broker needs a TopLevel).</param>
    public WebViewLoginProvider(Func<TopLevel?> topLevel)
    { _topLevel = topLevel; }

    public async Task<string> AcquireAuthorizationCodeAsync(Uri loginUrl, CancellationToken ct = default)
    {
        // Marshal the whole broker interaction onto the UI thread (native windows must be created there).
        // BACKGROUND priority: the WebView cold start is seconds of UI-thread work, and Background sits
        // below Render so the connect scrim paints before the stall.
        var callback = await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var owner = _topLevel();
            if (owner is null)
                throw new InvalidOperationException("Can't open the login window -- no active window found.");

            // BELT AND BRACES for the redirect: on macOS the broker does not recognize the WKWebView's
            // navigation to the redirect URI - GOG shows on_login_success WITH the code in the URL and the
            // dialog just sits there (MacinCloud, 2026-08-28). Watch the dialog's own navigation events,
            // capture the redirect ourselves, and close the dialog; if the broker then reports "closed"
            // instead of a callback, the captured URI is the answer it missed. Harmless on platforms where
            // the broker works: it completes first and the capture is never consulted.
            Uri? captured = null;

            var options = new Avalonia.Controls.WebAuthenticatorOptions(
                RequestUri: loginUrl,
                RedirectUri: new Uri(GogOAuth.RedirectUri))
            {
                NonPersistent = true,   // ALWAYS throwaway; see the class comment
                // WITHOUT this, Apple platforms use ASWebAuthenticationSession instead of the dialog: a
                // system window (with a URL bar) that only completes on CUSTOM-SCHEME callbacks - GOG's
                // https redirect can never terminate it, so login succeeded and the window sat there
                // forever (MacinCloud 2026-08-28). True = the same dialog path Windows/Linux already use,
                // where the factory below and its redirect capture actually run.
                PreferNativeWebDialog = true,
                // Build the native login dialog so it opens titled and at login-card size; the window is
                // created right after this factory returns, so queue resize/center at Send priority.
                NativeWebDialogFactory = () =>
                {
                    var dlg = new NativeWebDialog { Title = "Sign in to GOG", CanUserResize = true };
                    _activeDialog = dlg;   // cleared in the finally below; lets the scrim's Cancel reach it
                    void CaptureRedirect(Uri? u)
                    {
                        if (u is null || captured is not null) return;
                        if (!u.OriginalString.StartsWith(GogOAuth.RedirectUri, StringComparison.OrdinalIgnoreCase)) return;
                        captured = u;
                        Dispatcher.UIThread.Post(() => { try { dlg.Close(); } catch { /* already gone */ } });
                    }
                    // Both events, because which one fires for a redirect differs per platform backend.
                    dlg.NavigationStarted += (_, e) => CaptureRedirect(e.Request);
                    dlg.NavigationCompleted += (_, e) => CaptureRedirect(e.Request);
                    // The GOG page requests a popup during login; unhandled, the macOS adapter hands it to
                    // the DEFAULT BROWSER (a stray Chrome window appeared beside the dialog). Keep the
                    // whole flow inside the dialog instead.
                    dlg.NewWindowRequested += (_, e) =>
                    {
                        e.Handled = true;
                        if (e.Request is { } u) { try { dlg.Navigate(u); } catch { /* best-effort */ } }
                    };
                    Dispatcher.UIThread.Post(() =>
                    {
                        try
                        {
                            dlg.Resize(LoginWidth, LoginHeight);
                            // Center on the owner window's screen (pixel space, DPI-consistent).
                            if (owner is Window w && w.Screens?.ScreenFromWindow(w) is { } scr)
                            {
                                var a = scr.WorkingArea;
                                dlg.Move(a.X + (a.Width - LoginWidth) / 2, a.Y + (a.Height - LoginHeight) / 2);
                            }
                        }
                        catch { /* best-effort: sizing is cosmetic, never fail the login over it */ }
                    }, DispatcherPriority.Send);
                    return dlg;
                },
            };

            try
            {
                var result = await Avalonia.Controls.WebAuthenticationBroker.AuthenticateAsync(owner, options);
                // The capture is the answer the broker missed (see above). It was recorded and then never read:
                // the dialog closed itself on the redirect and the login was still reported as cancelled
                // (sweep 2 #24).
                return result?.CallbackUri ?? captured
                    ?? throw new OperationCanceledException("Login was closed before it completed.");
            }
            // The broker may THROW rather than return null when its dialog is closed under it; with the
            // redirect in hand that is a finished login, not a failure.
            catch (Exception) when (captured is not null) { return captured!; }
            finally { _activeDialog = null; }
        }, DispatcherPriority.Background);

        var code = GogOAuth.ExtractCode(callback.ToString())
            ?? throw new InvalidOperationException("GOG's response didn't contain a login code. Please try again.");

        return code;
    }
}
