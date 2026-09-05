import type { ReactNode } from 'react'

/**
 * A framed message with a heading: an error, a caveat, a note about what was found.
 *
 * `tone` is a name for what the message IS, not for a colour, so that a later change of
 * palette is a change in styles.css and nowhere else.
 */
export type BannerTone = 'warning' | 'note' | 'info'

export function StatusBanner({
  tone,
  title,
  role,
  children,
}: {
  tone: BannerTone
  title: string
  /**
   * 'alert' for a failure that has just happened and needs saying out loud. Left unset for
   * anything already announced by the page's own live region, so nothing is read twice.
   */
  role?: 'alert' | 'status'
  children?: ReactNode
}) {
  return (
    <section className={`banner banner-${tone}`} role={role}>
      <h2>{title}</h2>
      {children}
    </section>
  )
}
