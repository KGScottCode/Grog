// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Tests;

using Grog.Core.Auth;
using Grog.Core.Tests.Framework;

public class GogFormLoginParsingTests
{
    [Test]
    void ExtractLoginToken_FindsHiddenInput()
    {
        const string html = """
            <form method="post" action="/login_check">
              <input type="text" name="login[username]" />
              <input type="password" name="login[password]" />
              <input type="hidden" name="login[_token]" value="csrf-abc-123" />
            </form>
            """;
        Assert.Equal("csrf-abc-123", GogFormLogin.ExtractLoginToken(html), "token value");
    }

    [Test]
    void ExtractLoginToken_HandlesReversedAttributeOrder()
    {
        const string html = """<input value="rev-456" type="hidden" name="login[_token]">""";
        Assert.Equal("rev-456", GogFormLogin.ExtractLoginToken(html), "value-before-name variant");
    }

    [Test]
    void ExtractLoginToken_ReturnsNullWhenMissing()
    {
        Assert.Null(GogFormLogin.ExtractLoginToken("<html><body>No form here</body></html>"),
            "no token input -> null");
        Assert.Null(GogFormLogin.ExtractLoginToken("""<input name="login[_token]" value="">"""),
            "empty value -> null");
    }

    [Test]
    void ExtractTwoStepToken_FindsSecondStepInput()
    {
        const string html = """
            <form action="/login/two_step">
              <input name="second_step_authentication[token][letter_1]" />
              <input type="hidden" name="second_step_authentication[_token]" value="two-step-tok" />
            </form>
            """;
        Assert.Equal("two-step-tok", GogFormLogin.ExtractTwoStepToken(html), "two-step token");
    }

    [Test]
    void ContainsCaptcha_DetectsRecaptchaMarkers()
    {
        Assert.True(GogFormLogin.ContainsCaptcha(
            """<div class="g-recaptcha" data-sitekey="x"></div>"""), "g-recaptcha div");
        Assert.True(GogFormLogin.ContainsCaptcha(
            """<script src="https://www.google.com/recaptcha/api.js"></script>"""), "recaptcha api script");
        Assert.False(GogFormLogin.ContainsCaptcha(
            "<html><body>plain login form</body></html>"), "clean page -> false");
    }
}
