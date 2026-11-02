using System.Security.Claims;
using Lims.Host.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Lims.Host.Pages;

public sealed class LoginModel(UserStore users) : PageModel
{
    [BindProperty] public string Username { get; set; } = "";
    [BindProperty] public string Password { get; set; } = "";
    [BindProperty] public string? Code { get; set; }
    public string? Error { get; private set; }
    public string? EnrollSecret { get; private set; }
    public bool ShowCode { get; private set; }

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync(string? returnUrl, CancellationToken ct)
    {
        var r = await users.SignInAsync(Username, Password, Code, HttpContext.Connection.RemoteIpAddress?.ToString(), ct);
        switch (r.Status)
        {
            case LoginStatus.Ok:
                var claims = new List<Claim>
                {
                    new(ClaimTypes.Name, r.User!.Username),
                    new("display_name", r.User.DisplayName),
                    new("auth_time", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture)),
                };
                claims.AddRange(r.User.Roles.Select(role => new Claim(ClaimTypes.Role, role)));
                await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
                    new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)));
                return LocalRedirect(Url.IsLocalUrl(returnUrl) ? returnUrl! : "/");
            case LoginStatus.MfaEnrollmentRequired:
                EnrollSecret = r.NewTotpSecret; ShowCode = true; Error = Code is null ? null : "That code did not match.";
                break;
            case LoginStatus.MfaRequired:
                ShowCode = true; Error = "Enter your authenticator code.";
                break;
            case LoginStatus.MfaInvalid:
                ShowCode = true; Error = "That code did not match.";
                break;
            case LoginStatus.LockedOut:
                Error = "This account is locked for 15 minutes after repeated failures.";
                break;
            default:
                Error = "Username or password is wrong.";
                break;
        }
        return Page();
    }
}
