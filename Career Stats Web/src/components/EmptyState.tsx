/**
 * What the page says before anybody has searched.
 *
 * Not decoration and not an apology: it names what this box accepts, in the vocabulary of
 * the game that is currently selected, because "no results" and "I do not know what to
 * type here" are different problems and only one of them is the visitor's fault.
 *
 * Sample handles appear only when the backend has said it is serving fixtures. Against a
 * live service they would be an invitation to search for players who may not exist.
 */
export function EmptyState({
  gameName,
  accepts,
  samples,
  onPickSample,
}: {
  gameName: string
  /** The forms of identity this game's API will resolve, most reliable last. */
  accepts: readonly string[]
  samples: readonly string[]
  onPickSample: (sample: string) => void
}) {
  return (
    <div className="empty">
      <h2>Search a {gameName} career</h2>
      <p>
        Type a player above and the whole career comes back: headline numbers with their
        trend, recent matches, and where the time actually went.
      </p>
      <ul>
        {accepts.map((item) => (
          <li key={item}>{item}</li>
        ))}
      </ul>

      {samples.length > 0 && (
        <div className="empty-samples">
          <h3>Players in this sample set</h3>
          <div className="chips">
            {samples.map((sample) => (
              <button
                type="button"
                className="chip"
                key={sample}
                onClick={() => onPickSample(sample)}
              >
                {sample}
              </button>
            ))}
          </div>
        </div>
      )}
    </div>
  )
}
