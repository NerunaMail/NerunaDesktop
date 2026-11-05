using System.Net;
using Neruna.Core;
using Neruna.Core.Auth;

namespace Neruna.Client.Tests;

public class SignInErrorsTests
{
    [Fact]
    public void Expired_or_refused_sign_ins_ask_for_signing_in_again()
    {
        Assert.True(SignInErrors.NeedsSignIn(new OAuthException("abgelaufen", signInRequired: true)));
        Assert.True(SignInErrors.NeedsSignIn(new InvalidOperationException("Sync", new OAuthException("abgelaufen", signInRequired: true))));
        Assert.True(SignInErrors.NeedsSignIn(new SignInRejectedException("DAV 401")));
        Assert.True(SignInErrors.NeedsSignIn(new HttpRequestException("401", null, HttpStatusCode.Unauthorized)));
        Assert.True(SignInErrors.NeedsSignIn(new MailKit.Security.AuthenticationException("LOGIN failed")));
        Assert.True(SignInErrors.NeedsSignIn(new AggregateException(new IOException(), new OAuthException("x", signInRequired: true))));
    }

    [Fact]
    public void Other_errors_do_not()
    {
        Assert.False(SignInErrors.NeedsSignIn(new OAuthException("Anmeldung fehlgeschlagen: access_denied")));
        // A TLS handshake that failed: signing in would not help.
        Assert.False(SignInErrors.NeedsSignIn(new System.Security.Authentication.AuthenticationException("TLS")));
        Assert.False(SignInErrors.NeedsSignIn(new HttpRequestException("503", null, HttpStatusCode.ServiceUnavailable)));
        Assert.False(SignInErrors.NeedsSignIn(new TimeoutException()));
    }
}
