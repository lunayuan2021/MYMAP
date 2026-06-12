# Lychee Replay Engine Gap Audit 2026-06-12

## Already Present

- 3D replay scene with 15 station anchors, route edges, terrain props, water-route visuals, labels, and convoy movement.
- Replay file playback for 600 rounds from `reply_full_coverage_unity_v2.txt`.
- Runtime team HUD with red/blue captain portraits, roster counts, score, freshness, fruit, squad, and score bars.
- Start roster presentation: one captain model plus generated member models based on team size.
- Squad dispatch visual: generated squad member models move from source node to target node.
- Central illustration overlay for window contests and now broader key replay events.
- Camera follow controller with oblique fixed angle and 1.2x closer replay framing.

## Partially Done

- Water, road, and node alignment has been improved, but the map still needs repeated near-ground visual validation against the 3D map acceptance rules.
- Weather events are parsed and now illustrated, but there are no persistent weather-region VFX layers yet.
- Task, bounty, resource, guard, verify, and delivery events have pulses and illustration cards, but do not yet have full persistent board panels or map icons.
- Window contest has a central illustration and three reveal beats, but not a full dedicated three-card/tactic UI with explicit per-beat cost consumption.
- Team HUD shows main status changes, but inventory, guard action points, penalties, and final score breakdown are not fully exposed.

## Still Missing

- Real gameplay/action submission loop: move, resource usage, guard setup, squad commands, verify, deliver, invalid action handling.
- Authoritative rules engine/server integration. Replay currently displays already-settled state and should not be mistaken for rule simulation.
- Agent API/live-match visibility rules: hidden resources, future weather, future replay frames, and public/private payload separation.
- Persistent UI panels for resources, active tasks, bounties, weather forecast, guard states, and final over settlement.
- Full weather VFX by configured weather regions.
- Replay controls suitable for review: timeline seek, speed, pause, jump to next event, event log.
- Automated map validation report for water height, shore coverage, road retopology, props intersecting river, and old patch-object residue.
- Final polish pass toward the intended competitive MOBA-style presentation: stronger event transitions, hit flashes, camera beats, sound cues, and denser but clean status UI.
