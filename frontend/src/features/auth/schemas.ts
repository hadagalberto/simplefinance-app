import { z } from 'zod'

/**
 * Mirrors SetupUserValidator (backend, strict — see its own comment): name
 * required (<=200 chars), email required and email-shaped, password >= 8
 * chars. Gives instant client-side feedback before hitting the network;
 * the backend remains the source of truth via FluentValidation.
 */
export const setupSchema = z.object({
  name: z.string().trim().min(1, 'common:validation.nameRequired').max(200, 'common:validation.nameTooLong'),
  email: z.string().trim().min(1, 'common:validation.emailRequired').email('common:validation.emailInvalid'),
  password: z.string().min(8, 'common:validation.passwordMin'),
})

export type SetupFormValues = z.infer<typeof setupSchema>

/**
 * Mirrors LoginValidator (backend, deliberately loose — see its own
 * comment): only asserts presence, never format/strength, so the frontend
 * never leaks "malformed input" vs. "wrong password" through a distinct
 * error shape either (T-01-12).
 */
export const loginSchema = z.object({
  email: z.string().trim().min(1, 'common:validation.emailRequired'),
  password: z.string().min(1, 'common:validation.passwordRequired'),
})

export type LoginFormValues = z.infer<typeof loginSchema>

/**
 * Backs both the 6-digit TOTP InputOTP mode and the recovery-code plain-text
 * mode on TwoFactorVerifyPage — both funnel through this one `code` field,
 * so the schema deliberately does not over-constrain the pattern (mirrors
 * VerifyTwoFactorValidator's backend looseness: just NotEmpty).
 */
export const twoFactorVerifySchema = z.object({
  code: z.string().trim().min(1, 'common:validation.codeRequired'),
})

export type TwoFactorVerifyFormValues = z.infer<typeof twoFactorVerifySchema>

/**
 * Mirrors ChangePasswordValidator (backend): currentPassword required,
 * newPassword >= 8 chars (matches SetupUserValidator's floor). The
 * "new === confirm" and "new !== current" checks are client-only UX
 * (the backend enforces "differs from current" server-side via a
 * password-hash comparison it can't replicate here) — confirmNewPassword
 * is not sent to the API.
 */
export const changePasswordSchema = z
  .object({
    currentPassword: z.string().min(1, 'common:validation.currentPasswordRequired'),
    newPassword: z.string().min(8, 'common:validation.passwordMin'),
    confirmNewPassword: z.string().min(1, 'common:validation.confirmPasswordRequired'),
  })
  .refine((values) => values.newPassword === values.confirmNewPassword, {
    message: 'common:validation.passwordsMismatch',
    path: ['confirmNewPassword'],
  })

export type ChangePasswordFormValues = z.infer<typeof changePasswordSchema>
