// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Grog.Core.Auth;

/// <summary>Headless credential login: scripts GOG's own login form and yields the OAuth authorization code.
/// PRIVACY CONTRACT: username/password exist only as parameters for the HTTP exchange -- never persisted,
/// logged, or outliving the call; the only artifact is the token pair stored by ITokenStore.
/// A reCAPTCHA makes scripted login impossible: CaptchaRequiredException, fall back to the browser flow.</summary>
public sealed class GogFormLogin
{
    private const string LoginCheckUrl = "https://login.gog.com/login_check";
    private const string TwoStepUrl = "https://login.gog.com/login/two_step";

    /// <param name="twoFactorCodePrompt">Called when GOG emails a 4-character security code;
    /// returns the code the user received.</param>
    public static async Task<string> LoginAsync(
        string username,
        string password,
        Func<CancellationToken, Task<string>> twoFactorCodePrompt,
        CancellationToken ct = default)
    {
        var handler = new HttpClientHandler
        {
            CookieContainer = new CookieContainer(),
            AllowAutoRedirect = true,
            UseCookies = true,
        };
        using var http = new HttpClient(handler);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; Grog/0.1)");

        // 1. Load the login form (authorize URL redirects to login.gog.com).
        using var formResponse = await http.GetAsync(GogOAuth.AuthorizeUrl, ct);
        var formHtml = await formResponse.Content.ReadAsStringAsync(ct);

        if (ContainsCaptcha(formHtml))
            throw new CaptchaRequiredException();

        var loginToken = ExtractLoginToken(formHtml)
            ?? throw new LoginFlowChangedException("login[_token] not found on the login page");

        // 2. Submit credentials.
        using var loginResponse = await http.PostAsync(LoginCheckUrl, new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["login[username]"] = username,
                ["login[password]"] = password,
                ["login[login_flow]"] = "default",
                ["login[_token]"] = loginToken,
            }), ct);

        var landedUrl = loginResponse.RequestMessage?.RequestUri?.ToString() ?? "";
        var landedHtml = await loginResponse.Content.ReadAsStringAsync(ct);

        // 3. Two-step (emailed 4-character code)?
        if (landedUrl.Contains("two_step", StringComparison.OrdinalIgnoreCase))
        {
            var twoStepToken = ExtractTwoStepToken(landedHtml)
                ?? throw new LoginFlowChangedException("second_step_authentication[_token] not found");

            var code = (await twoFactorCodePrompt(ct)).Trim();
            if (code.Length != 4)
                throw new InvalidCredentialsException("The GOG security code is exactly 4 characters.");

            using var twoStepResponse = await http.PostAsync(TwoStepUrl, new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["second_step_authentication[token][letter_1]"] = code[0].ToString(),
                    ["second_step_authentication[token][letter_2]"] = code[1].ToString(),
                    ["second_step_authentication[token][letter_3]"] = code[2].ToString(),
                    ["second_step_authentication[token][letter_4]"] = code[3].ToString(),
                    ["second_step_authentication[send]"] = "",
                    ["second_step_authentication[_token]"] = twoStepToken,
                }), ct);

            landedUrl = twoStepResponse.RequestMessage?.RequestUri?.ToString() ?? "";
            landedHtml = await twoStepResponse.Content.ReadAsStringAsync(ct);
        }

        // 4. Success lands on on_login_success?code=...
        var authCode = GogOAuth.ExtractCode(landedUrl);
        if (authCode is not null && landedUrl.Contains("on_login_success", StringComparison.OrdinalIgnoreCase))
            return authCode;

        if (ContainsCaptcha(landedHtml))
            throw new CaptchaRequiredException();

        throw new InvalidCredentialsException(
            "GOG did not accept the login (wrong username/password, or the account requires the browser flow).");
    }

    // --- parsers (internal for unit testing) ---

    internal static string? ExtractLoginToken(string html)
        => ExtractHiddenInput(html, "login[_token]");

    internal static string? ExtractTwoStepToken(string html)
        => ExtractHiddenInput(html, "second_step_authentication[_token]");

    internal static bool ContainsCaptcha(string html)
        => html.Contains("g-recaptcha", StringComparison.OrdinalIgnoreCase)
        || html.Contains("recaptcha/api", StringComparison.OrdinalIgnoreCase);

    private static string? ExtractHiddenInput(string html, string name)
    {
        // Attribute order varies; match name=... value=... in either order within one input tag.
        var escaped = Regex.Escape(name);
        var m = Regex.Match(html,
            $"""<input[^>]*name="{escaped}"[^>]*value="(?<v>[^"]*)"[^>]*>""",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!m.Success)
            m = Regex.Match(html,
                $"""<input[^>]*value="(?<v>[^"]*)"[^>]*name="{escaped}"[^>]*>""",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return m.Success && m.Groups["v"].Value.Length > 0 ? m.Groups["v"].Value : null;
    }
}
