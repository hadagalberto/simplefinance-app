using FluentValidation;

namespace SimpleFinance.Application.Auth.ChangePassword;

/// <summary>
/// Validates the change-password command. "New password must differ from current" can't be
/// checked by comparing raw strings (the stored value is a hash), so the endpoint passes the
/// caller's existing PasswordHash into FluentValidation's RootContextData under
/// <see cref="ExistingPasswordHashKey"/> before validating — this rule reads it back via
/// IPasswordHasher.Verify. If the caller omits that context data (e.g. a unit test validating
/// the command in isolation), the rule is skipped rather than failing closed, since "current
/// password verification" itself happens separately in the endpoint (AuthEndpoints.ChangePasswordAsync).
/// </summary>
public sealed class ChangePasswordValidator : AbstractValidator<ChangePasswordCommand>
{
    public const string ExistingPasswordHashKey = "ExistingPasswordHash";

    // Matches SetupUserValidator's floor — see its comment for the rationale.
    private const int MinimumPasswordLength = 8;

    private readonly IPasswordHasher _passwordHasher;

    public ChangePasswordValidator(IPasswordHasher passwordHasher)
    {
        _passwordHasher = passwordHasher;

        RuleFor(x => x.CurrentPassword).NotEmpty();

        RuleFor(x => x.NewPassword)
            .NotEmpty()
            .MinimumLength(MinimumPasswordLength)
            .WithMessage($"'New Password' must be at least {MinimumPasswordLength} characters long.");

        RuleFor(x => x.NewPassword)
            .Must(NotSameAsExistingPassword)
            .WithMessage("'New Password' must be different from the current password.")
            .WithErrorCode("new_password_same_as_current")
            .When(x => !string.IsNullOrEmpty(x.NewPassword));
    }

    private bool NotSameAsExistingPassword(ChangePasswordCommand command, string newPassword, ValidationContext<ChangePasswordCommand> context)
    {
        if (!context.RootContextData.TryGetValue(ExistingPasswordHashKey, out var existingHashObj)
            || existingHashObj is not string existingHash)
        {
            return true;
        }

        return !_passwordHasher.Verify(newPassword, existingHash);
    }
}
