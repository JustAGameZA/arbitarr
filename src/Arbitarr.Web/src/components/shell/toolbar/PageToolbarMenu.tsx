import { useEffect, useId, useRef, useState, type ReactNode } from 'react';

import styles from './PageToolbar.module.css';

interface PageToolbarMenuProps {
  /**
   * The trigger's text. Callers include the active selection ("Kind:
   * Decisions") so the current filter is readable without opening the menu —
   * that readability is what the native <select> it replaces provided for free,
   * and losing it is the regression a disclosure button invites.
   */
  label: string;
  /** Which edge of the trigger the panel aligns to. */
  align?: 'start' | 'end';
  children: ReactNode;
}

/**
 * The project's first dropdown primitive: a disclosure button and a role="menu"
 * panel.
 *
 * The closed panel is not rendered at all rather than hidden with CSS. A
 * `display: none` panel still holds focusable children in some engines, and the
 * whole point of the Escape/outside-click handling below is that focus never
 * ends up somewhere the user cannot see.
 */
export function PageToolbarMenu({ label, align = 'start', children }: PageToolbarMenuProps) {
  const [open, setOpen] = useState(false);
  const panelId = useId();
  const containerRef = useRef<HTMLDivElement>(null);
  const triggerRef = useRef<HTMLButtonElement>(null);

  useEffect(() => {
    if (!open) {
      return;
    }

    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        setOpen(false);
        // Escape must put focus back where the user left it. Without this the
        // focus ring lands on <body> and keyboard users restart from the top of
        // the page every time they dismiss a menu.
        triggerRef.current?.focus();
      }
    };

    const onPointerDown = (event: PointerEvent) => {
      const target = event.target;
      if (target instanceof Node && containerRef.current?.contains(target) === true) {
        return;
      }
      setOpen(false);
    };

    document.addEventListener('keydown', onKeyDown);
    document.addEventListener('pointerdown', onPointerDown);

    return () => {
      document.removeEventListener('keydown', onKeyDown);
      document.removeEventListener('pointerdown', onPointerDown);
    };
  }, [open]);

  return (
    <div ref={containerRef} className={styles.menu}>
      <button
        ref={triggerRef}
        type="button"
        className={styles.button}
        aria-haspopup="menu"
        aria-expanded={open}
        aria-controls={open ? panelId : undefined}
        onClick={() => setOpen((current) => !current)}
      >
        {label}
      </button>

      {open && (
        <div
          id={panelId}
          role="menu"
          aria-label={label}
          className={align === 'end' ? styles.panelEnd : styles.panel}
          // A click anywhere inside the panel closes it. Item activation is the
          // normal path, but a click on a group's label should not leave the
          // menu stranded open either.
          onClick={() => setOpen(false)}
        >
          {children}
        </div>
      )}
    </div>
  );
}
