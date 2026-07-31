# Lychee Replay Extra GLB Manifest

Generated: 2026-06-12

This folder stores the supplemental 3D model assets requested by `补充素材清单_20260612.md`.

## Character Models

| Requirement | Source asset | Runtime path | Extra archive path | Notes |
| --- | --- | --- | --- | --- |
| Team captain model | `/Users/lunayuan/Documents/荔枝争霸赛/小队长.glb` | `Assets/LycheeReplayDemo/ModelsGLB/Captain.glb` | `Assets/LycheeReplayDemo/ModelsGLB/Extra/captain_rig.glb` | Existing runtime file matches the provided source GLB. |
| Regular team member model | `/Users/lunayuan/Documents/荔枝争霸赛/队员.glb` | `Assets/LycheeReplayDemo/ModelsGLB/Member.glb` | `Assets/LycheeReplayDemo/ModelsGLB/Extra/member_rig.glb` | Added as a separate standard entry for future replay logic. |
| Dispatched squad member model | `/Users/lunayuan/Documents/荔枝争霸赛/分队队员.glb` | `Assets/LycheeReplayDemo/ModelsGLB/SquadMember.glb` | `Assets/LycheeReplayDemo/ModelsGLB/Extra/squad_member.glb` | Existing runtime file matches the provided source GLB. |

## Supplemental Prop Models

| Requirement | Runtime source | Extra archive path | Concept reference |
| --- | --- | --- | --- |
| Guard barricade | `Assets/LycheeReplayDemo/ModelsGLB/guard_barricade.glb` | `Assets/LycheeReplayDemo/ModelsGLB/Extra/guard_barricade.glb` | `ConceptPNGs/guard_barricade_concept.png` |
| Flag pole | `Assets/LycheeReplayDemo/ModelsGLB/flag_pole.glb` | `Assets/LycheeReplayDemo/ModelsGLB/Extra/flag_pole.glb` | `ConceptPNGs/flag_pole_concept.png` |
| Task board | `Assets/LycheeReplayDemo/ModelsGLB/task_board.glb` | `Assets/LycheeReplayDemo/ModelsGLB/Extra/task_board.glb` | `ConceptPNGs/task_board_concept.png` |
| Bounty drum | `Assets/LycheeReplayDemo/ModelsGLB/bounty_drum.glb` | `Assets/LycheeReplayDemo/ModelsGLB/Extra/bounty_drum.glb` | `ConceptPNGs/bounty_drum_concept.png` |
| Open lychee crate | `Assets/LycheeReplayDemo/ModelsGLB/lychee_crate_open.glb` | `Assets/LycheeReplayDemo/ModelsGLB/Extra/lychee_crate_open.glb` | `ConceptPNGs/lychee_crate_open_concept.png` |

## Import Notes

- The provided character GLBs are large: approximately 78 MB to 99 MB each.
- The supplemental prop GLBs are approximately 52 MB to 61 MB each.
- If runtime loading or build size becomes heavy, these assets should be decimated or exported with Draco / mesh compression in the DCC pipeline.
