import { useId } from 'react';

import styles from './PageToolbar.module.css';

interface PageToolbarInputProps {
  /**
   * The input's visible label. Required, not optional, and rendered as a real
   * <label> rather than a placeholder: a placeholder disappears the moment the
   * operator types, so the only description of what the box filters on would be
   * gone exactly while it is filtering. It is also the accessible name the
   * surface tests query this control by.
   */
  label: string;
  value: string;
  onChange: (value: string) => void;
  /**
   * Called when Enter is pressed in the field, for callers that apply on an
   * explicit trigger rather than on every change.
   *
   * Optional, and not a debounce: a toolbar button is a `type="button"`, so a
   * field that replaced a `<form>` would otherwise lose the Enter-to-submit the
   * form gave it for free. Callers that filter live (Logs) simply omit it.
   */
  onSubmit?: () => void;
  placeholder?: string;
}

/**
 * A labelled text field sized for the toolbar row.
 *
 * The toolbar's menus replace native <select>s, but a free-text filter has no
 * menu shape -- Logs' message search and Suppressions' query key are both
 * open-ended strings, not a choice among known options. This is the primitive
 * for that case, and it is the reason the toolbar vocabulary needed a sixth
 * member rather than those two surfaces keeping a `.panel` each.
 *
 * Deliberately DUMB: it owns no timer and no submit semantics. Logs debounces
 * its value and resets the page; Suppressions applies on an explicit Apply
 * button. Those are different interaction models on purpose, and folding either
 * into this component would impose one surface's choice on the other -- so the
 * caller keeps the behaviour and this keeps only the markup.
 *
 * Like every other toolbar member it contributes NO heading: PageHeader owns the
 * single <h1> per route (AC2b), and the label here is a <label>, not a caption.
 */
export function PageToolbarInput({
  label,
  value,
  onChange,
  onSubmit,
  placeholder,
}: PageToolbarInputProps) {
  const inputId = useId();

  return (
    <span className={styles.field}>
      <label className={styles.fieldLabel} htmlFor={inputId}>
        {label}
      </label>
      <input
        id={inputId}
        type="text"
        className={styles.input}
        value={value}
        placeholder={placeholder}
        onChange={(event) => onChange(event.target.value)}
        onKeyDown={
          onSubmit === undefined
            ? undefined
            : (event) => {
                if (event.key === 'Enter') {
                  // The field is not inside a <form>, so nothing would submit
                  // and nothing needs preventing -- but a stray Enter must not
                  // bubble to an ancestor handler either.
                  event.preventDefault();
                  onSubmit();
                }
              }
        }
      />
    </span>
  );
}
