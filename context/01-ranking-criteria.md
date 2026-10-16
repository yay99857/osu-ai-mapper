# Ranking criteria (official rules)

The osu! team publishes rules (must) and guidelines (should, unless there is a reason) for maps to
be ranked. They are the strongest consensus we have about what a readable, fair map is.

## All modes

- **Timing** must be exact; all difficulties of a set share the same red lines; add red lines only
  for real tempo or metre changes.
- **Snapping**: every object within 2 ms of a timeline tick of its divisor.
- **Kiai** starts on a sound in the music and is used for the strongest part, usually the chorus.
- **Spread**: a set starts from a difficulty low enough for its drain time (under 2:30 the lowest
  may be a Normal; longer songs may start higher), with no skipped levels; each difficulty has at
  least 30 s of drain.
- Every clicked object needs an audible hitsound; preview point identical across difficulties.

## osu!standard, by difficulty

| | Rhythm | Overlap | Spinners | AR / OD / HP / CS |
|---|---|---|---|---|
| Easy | 1/1 and 2/1, no 1/4 | nothing 1 beat apart or closer fully overlapping | ≥ 4 beats, 4 beats of rest after | AR ≤ 5, OD/HP 1-3, CS ≤ 4 |
| Normal | mostly 1/1, some 1/2 | as Easy; 1/2 overlaps from distance snap are fine | ≥ 3 beats, 2 beats after | AR 4-6, OD/HP 3-5, CS ≤ 5 |
| Hard | mostly 1/2, occasional 1/4; streams at most 5 notes | nothing 1/2 apart or closer fully overlapping | ≥ 2 beats, 1 beat after | AR 6-8, OD 5-7, HP 4-6, CS ≤ 6 |
| Insane | free; 1/4 patterns normal | avoid full overlap of 1/4 objects in simpler Insanes | — | AR 7-9.3, OD 7-9, HP 5-8, CS ≤ 7 |
| Expert | anything | — | — | AR/OD ≥ 8, HP ≥ 5, CS ≤ 7 |

Further points worth keeping in mind:

- Slider ends follow the music; circles and slider heads need a sound to justify them.
- Easy/Normal: time-distance equality (spacing proportional to time), distance snap about
  0.8-1.3x, no very short or multi-reverse sliders, readable reverse arrows.
- Hard: different rhythms should look different (spacing must not make 1/2 and 1/1 identical).
- Insane: cross-screen jumps or non-overlapping fast streams are Expert techniques.
- All: slider paths readable, reverse arrows not hidden; combo colours neither too dark nor too
  bright; spinners long enough for Auto to reach the full bonus.

## For our project

Use these references to review Mapperatorinator output against the music and human maps.
The active engine supplies rhythm and placement. See
[the mapping principles](../docs/mapping-principles.md) and
[the architecture](../docs/architecture.md).

Sources: [Ranking criteria](https://osu.ppy.sh/wiki/en/Ranking_criteria),
[osu! ranking criteria](https://osu.ppy.sh/wiki/en/Ranking_criteria/osu!),
[Kiai time](https://osu.ppy.sh/wiki/en/Gameplay/Kiai_time).
