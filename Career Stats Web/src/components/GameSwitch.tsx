import { GAMES } from '../lib/api'
import type { GameKey } from '../types'

/**
 * Swapping games.
 *
 * Real radio inputs in one named group, visually a segmented control. That is not
 * fastidiousness: it is how arrow-key navigation, the "2 of 2" announcement, and Windows
 * high-contrast mode arrive for free. A row of buttons with aria-pressed would have to
 * reimplement all three, badly.
 */
export function GameSwitch({
  value,
  onChange,
}: {
  value: GameKey
  onChange: (game: GameKey) => void
}) {
  return (
    <fieldset className="gameswitch">
      <legend className="sr-only">Game</legend>
      {GAMES.map((game) => (
        <label className="gameswitch-option" key={game.key}>
          <input
            type="radio"
            name="game"
            value={game.key}
            checked={game.key === value}
            onChange={() => onChange(game.key)}
          />
          <span>{game.name}</span>
        </label>
      ))}
    </fieldset>
  )
}
