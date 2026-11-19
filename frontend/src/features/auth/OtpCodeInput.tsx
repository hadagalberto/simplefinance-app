import { useId, useRef, type ChangeEvent, type ClipboardEvent, type KeyboardEvent } from 'react'
import { FormLabel } from 'react-bootstrap'
import { useTranslation } from 'react-i18next'

import { fillOtpDigits } from '@/features/auth/authInputLogic'

type OtpCodeInputProps = {
  code: string[]
  setCode: (next: string[]) => void
  label?: string
  labelClassName?: string
  inputClassName?: string
}

const DIGIT = /^[0-9]$/

/**
 * One-time code entry: one numeric box per slot of `code`. Typing a digit
 * fills the box and moves on, Backspace in an empty box steps back, the arrow
 * keys move between boxes, and pasting writes every digit of the clipboard
 * from the focused box onward.
 */
export function OtpCodeInput({ code, setCode, label, labelClassName, inputClassName }: OtpCodeInputProps) {
  const { t } = useTranslation('auth')
  const labelId = useId()
  const boxes = useRef<Array<HTMLInputElement | null>>([])
  const total = code.length

  function focusBox(index: number) {
    const box = boxes.current[Math.min(Math.max(index, 0), total - 1)]
    box?.focus()
    box?.select()
  }

  function writeSlot(index: number, value: string) {
    const next = [...code]
    next[index] = value
    setCode(next)
  }

  function handleChange(index: number, event: ChangeEvent<HTMLInputElement>) {
    const value = event.target.value
    if (value === '') {
      writeSlot(index, '')
      return
    }
    const digits = value.replace(/[^0-9]/g, '')
    if (digits === '') return
    writeSlot(index, digits[digits.length - 1])
    if (index < total - 1) focusBox(index + 1)
  }

  function handleKeyDown(index: number, event: KeyboardEvent<HTMLInputElement>) {
    if (DIGIT.test(event.key) && !event.ctrlKey && !event.metaKey && !event.altKey) {
      // Replace the slot even when it already holds a digit (maxLength would block the change).
      event.preventDefault()
      writeSlot(index, event.key)
      if (index < total - 1) focusBox(index + 1)
      return
    }
    if (event.key === 'Backspace' && code[index] === '' && index > 0) {
      event.preventDefault()
      writeSlot(index - 1, '')
      focusBox(index - 1)
      return
    }
    if (event.key === 'ArrowLeft' && index > 0) {
      event.preventDefault()
      focusBox(index - 1)
      return
    }
    if (event.key === 'ArrowRight' && index < total - 1) {
      event.preventDefault()
      focusBox(index + 1)
    }
  }

  function handlePaste(index: number, event: ClipboardEvent<HTMLInputElement>) {
    event.preventDefault()
    const text = event.clipboardData.getData('text')
    const digitCount = text.replace(/[^0-9]/g, '').length
    if (digitCount === 0) return
    setCode(fillOtpDigits(code, index, text))
    focusBox(index + digitCount)
  }

  return (
    <>
      <FormLabel id={labelId} className={labelClassName}>
        {label}
        &nbsp;<span className="text-danger">*</span>
      </FormLabel>
      <div className="sf-otp" role="group" aria-labelledby={labelId}>
        {code.map((digit, index) => (
          <input
            key={index}
            ref={(element) => {
              boxes.current[index] = element
            }}
            type="text"
            className={inputClassName ? `form-control sf-otp__digit ${inputClassName}` : 'form-control sf-otp__digit'}
            inputMode="numeric"
            pattern="[0-9]*"
            maxLength={1}
            autoComplete={index === 0 ? 'one-time-code' : 'off'}
            aria-label={t('otp.digitLabel', { n: index + 1, total })}
            value={digit}
            onFocus={(event) => event.target.select()}
            onChange={(event) => handleChange(index, event)}
            onKeyDown={(event) => handleKeyDown(index, event)}
            onPaste={(event) => handlePaste(index, event)}
          />
        ))}
      </div>
    </>
  )
}

export default OtpCodeInput
