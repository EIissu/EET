import type { RefObject } from 'react'

/**
 * The search box.
 *
 * A real <form> with a real <label>, so Enter submits, the mobile keyboard shows a search
 * key, and clicking the label focuses the field. The placeholder is a sample of the input,
 * never the label -- placeholder text disappears the moment somebody types, which is
 * exactly when they might want to reread it.
 */
export function SearchBar({
  value,
  onValueChange,
  onSubmit,
  gameName,
  hint,
  busy,
  inputRef,
}: {
  value: string
  onValueChange: (value: string) => void
  onSubmit: () => void
  gameName: string
  /** From GAMES: what this game accepts, in that game's own vocabulary. */
  hint: string
  busy: boolean
  /** So an error can hand the text back for editing, with the cursor already in it. */
  inputRef: RefObject<HTMLInputElement | null>
}) {
  return (
    <form
      className="search"
      role="search"
      onSubmit={(event) => {
        event.preventDefault()
        onSubmit()
      }}
    >
      <label className="search-label" htmlFor="player-query">
        Search {gameName}
      </label>
      <div className="search-row">
        <input
          id="player-query"
          ref={inputRef}
          type="search"
          value={value}
          onChange={(event) => onValueChange(event.target.value)}
          placeholder={hint}
          aria-describedby="search-hint"
          autoComplete="off"
          autoCorrect="off"
          autoCapitalize="none"
          spellCheck={false}
          enterKeyHint="search"
        />
        <button type="submit" disabled={busy || value.trim().length === 0}>
          {busy ? 'Searching…' : 'Search'}
        </button>
      </div>
      <p className="search-hint" id="search-hint">
        Enter a {hint}.
      </p>
    </form>
  )
}
