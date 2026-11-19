using FluentValidation;

namespace SimpleFinance.Application.Auth.Setup;

/// <summary>
/// Validates the first-run setup command. Unlike LoginValidator (deliberately loose — see its
/// own comment), this validator is strict: at setup time there is no "don't leak which field is
/// wrong" concern, since there is no existing account to probe against yet.
/// </summary>
public sealed class SetupUserValidator : AbstractValidator<SetupUserCommand>
{
    // 8 chars is a reasonable floor for a single-user, self-hosted app (not a public
    // multi-tenant SaaS) — matches STACK.md's Argon2id guidance without imposing composition
    // rules (uppercase/digit/symbol requirements) that mostly just annoy a single legitimate
    // user. ChangePasswordValidator reuses the same floor for consistency.
    private const int MinimumPasswordLength = 8;

    public SetupUserValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty()
            .MaximumLength(200);

        RuleFor(x => x.Email)
            .NotEmpty()
            .MaximumLength(320)
            .Must(BeAValidEmailAddress)
            .WithMessage("'Email' is not a valid email address.");

        RuleFor(x => x.Password)
            .NotEmpty()
            .MinimumLength(MinimumPasswordLength)
            .WithMessage($"'Password' must be at least {MinimumPasswordLength} characters long.");
    }

    private static bool BeAValidEmailAddress(string email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return false;
        }

        try
        {
            var parsed = new System.Net.Mail.MailAddress(email);
            return parsed.Address == email.Trim();
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
