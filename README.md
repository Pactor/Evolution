# Evolution

A hobby artificial-life simulator. Two teams of creatures forage, farm, fight over
territory and breed, and the traits that help them do it spread through the
population over generations. Occasionally a third or fourth lineage emerges from
the first two and takes the whole thing over.

C# / .NET Framework 4.8 / WinForms. No dependencies — open `Evolution.sln` in
Visual Studio and build, or:

```
msbuild Evolution.sln /p:Configuration=Release
```

## Running it

```
Evolution.exe                    watch a random world
Evolution.exe --seed 161         watch one specific world, replayable
Evolution.exe --sim              run 20 games headless and print a report
```

The simulation ticks every 200 ms on screen. A game usually takes two or three
minutes to watch. **Reset** starts a fresh world (the same one, if `--seed` was
given).

## Headless mode

The whole simulation lives in `Base/World.cs` and has no UI attached, so it can
run at full speed with nothing rendered. This is far and away the best way to
test a balance change: **roughly 300 complete games in two seconds**, versus three
minutes each on screen.

```
Evolution.exe --sim [runs] [maxTicks] [--out <file>]
```

| Argument | Default | Meaning |
| --- | --- | --- |
| `runs` | 20 | How many games to play. Game *n* uses seed *n*, so runs are reproducible. |
| `maxTicks` | 20000 | Tick ceiling per game. A game still running at this point is called a stalemate. |
| `--out <file>` | — | Also write the report to a file. |

Output goes to the calling console, so it pipes normally:

```
Evolution.exe --sim 300 20000 --out report.txt
Evolution.exe --sim 50 | more
```

Seeds are shared with `--seed`, which is the point of them: find an interesting
game in the report, then watch that exact game.

```
Evolution.exe --sim 300 --out report.txt      # seed 161 founds a hybrid lineage
Evolution.exe --seed 161                      # ...so go and watch it
```

### Reading the report

One row per game:

```
seed  ticks  outcome                       T0  T1  T2  T3  hyb  births  killed  tiles  planted  cross  lineages
 161    686  Team 2 (hybrid) wins! Popul…  19   3  25   -    1      40      12     60       10      3         1
```

`T0`–`T3` are final populations per lineage (`-` if that lineage never existed),
`hyb` is surviving outcasts, `tiles` is farm/irrigation tiles built, `planted` is
new forest clearings, `cross` is interbreedings and `lineages` is new teams
founded.

Then a summary, and finally the part worth watching:

```
ability share of surviving population (founder odds in brackets):
  sense poison : 0.67   (founders 0.50)
  farm         : 0.54   (founders 0.40)
```

Founders are seeded with each trait at a fixed probability. A final share above
that means the trait was **selected for** over the game. This is the number to
watch when changing anything — if a trait sits below its founder odds, whatever
it does is costing more than it is worth.

Typical output over 300 runs: an even win split, ~48% won on population, ~49% by
elimination, ~4% stalemates, median game 778 ticks, and every trait above its
founder odds.

## How it works

- **Foraging.** Wild food and water regrow at a trickle — enough to keep a
  population alive at subsistence, never enough to breed on.
- **Farming.** Carry a full belly to a forest clearing and the *farm* skill turns
  it into a food tile; arrive with your thirst slaked and *irrigate* turns it into
  water. Nothing is granted at spawn: every tile on the map was walked there.
  Tiles feed the pair that made them so they can stay and breed.
- **Territory.** Tiles are worth taking. A fighter eats from an enemy plot and
  wrecks the rest; hold a plot unopposed long enough and it changes hands, tiles
  and all. Fighting on your own plot hurts less.
- **Forest spreads.** Standing in a clearing leaves you carrying seed, which
  anyone who can *build a nest* plants once they have wandered far enough.
- **Breeding.** Two ready creatures of the same lineage, alone in a clearing.
  Children inherit both parents' brains with crossover and a chance of mutation.
- **Interbreeding.** Two creatures from *opposing* teams that can both *reason*,
  both in season and in the same clearing, don't fight — they interbreed. The
  child belongs to neither side, wears the blend of both colours, and is tolerated
  by everyone except the aggressive-without-reason. Two such outcasts who pair
  found a lineage of their own: hardier, faster-breeding, and hostile to all.
- **Winning.** First lineage to 25, or last one standing.

### Abilities

Brains are a flat list of `(id, level)` pairs indexed against `BrainSkills.txt`.
These are the ones wired up:

| Id | Ability | Effect |
| --- | --- | --- |
| 1–4 | Movement | Every creature has at least one |
| 8 | Can Reason | Won't fight another reasoner sharing a clearing; required to interbreed |
| 13 | Can Sense Direction of Poison | Steps around poison instead of dying in it |
| 18 | Can Reproduce | Required to breed |
| 21 | Can Fight | Answers intruders, raids enemy plots, can eat from them |
| 26 | Can Build a Nest | Plants new forest clearings |
| 28 | Can be Aggressive | Attacks outcasts, who everyone else tolerates |
| 31 / 32 | Can Farm / Can Irrigate | Turns a full belly / slaked thirst into a tile |

The rest of `BrainSkills.txt` is unimplemented — it's the original design sketch.

## Tuning

Constants live at the top of `Base/World.cs` (world rules, combat, capture,
regrowth) and `Base/Entity.cs` (needs, founder trait odds, inheritance, hybrid
vigour). Change one, run `--sim 300`, compare the summary and the ability shares.
