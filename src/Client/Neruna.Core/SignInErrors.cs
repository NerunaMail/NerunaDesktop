using System.Net;

namespace Neruna.Core;

/// <summary>An error that carries the HTTP status the server answered with (Graph, EWS, DAV).</summary>
public interface IHttpStatusError
{
    HttpStatusCode? HttpStatus { get; }
}

/// <summary>
/// The server refused the user name or password (a password changed, an app password deleted). Derived from
/// <see cref="System.Security.Authentication.AuthenticationException"/> as before, but told apart from TLS errors.
/// </summary>
public sealed class SignInRejectedException(string message) : System.Security.Authentication.AuthenticationException(message);

/// <summary>Which sync errors the user can only fix by signing in again (or entering the password anew).</summary>
public static class SignInErrors
{
    /// <summary>
    /// True for: a sign-in that expired or was revoked (OAuth: Microsoft 365, Google), a password the mail server
    /// refuses, HTTP 401 from DAV, Graph or EWS. Not for timeouts, TLS or server errors – signing in would not help.
    /// </summary>
    public static bool NeedsSignIn(Exception error)
    {
        for (var e = error; e is not null; e = e.InnerException)
        {
            if (e is Auth.OAuthException { SignInRequired: true } or SignInRejectedException
                or HttpRequestException { StatusCode: HttpStatusCode.Unauthorized }
                or IHttpStatusError { HttpStatus: HttpStatusCode.Unauthorized }
                // MailKit (IMAP, SMTP, ManageSieve): the server refused the login; Core does not reference MailKit.
                || e.GetType().FullName == "MailKit.Security.AuthenticationException")
            {
                return true;
            }

            if (e is AggregateException aggregate)
            {
                return aggregate.InnerExceptions.Any(NeedsSignIn);
            }
        }

        return false;
    }
}
