using FluentValidation;

namespace SimpleFinance.Application.Auth.Login;

/// <summary>
/// Deliberately loose compared to SetupUserValidator: login only asserts both fields are
/// present. It must NOT validate email format or password strength — doing so would let an
/// attacker distinguish "malformed input" from "wrong password" via a different error shape,
/// leaking information beyond the generic 401 the login endpoint returns for any credential
/// mismatch (T-01-12).
/// </summary>
public sealed class LoginValidator : AbstractValidator<LoginCommand>
{
    public LoginValidator()
    {
        RuleFor(x => x.Email).NotEmpty();
        RuleFor(x => x.Password).NotEmpty();
    }
}
