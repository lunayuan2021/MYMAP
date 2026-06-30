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

## Event VFX Overhead Models (20260629)

车队头顶事件特效的 3D 信标模型（取代旧 2D 图标贴图）。用户从图生工具导出高模（46.8万~100万面、各带 4 张 2048 PBR 贴图、25~39MB），
落地后由 `proc3.mjs`（gltf-transform：保 POSITION+NORMAL+TEXCOORD_0 → weld → meshopt simplify(error 0.01) →
丢法线/金属粗糙/自发光/AO 三张贴图、只留 baseColor 缩到 512 重编 JPEG → prune）减面到 ~3500 面、~250KB、自带一张 512 基础色贴图。
运行时用自定义 shader `Lychee/Overhead Event Model`：采样 baseColor 贴图 + 顶点法线平滑着色 + 淡队色 tint(往白 lerp 0.55，让贴图本色显出来) +
自发光 + 边缘 rim + ZTest Always（永不被挡，renderQueue 3100）。**用户要求保留贴图**，所以是带 UV/法线/贴图的版本（早期纯色无贴图 proc.mjs 版已弃）。
原始高模与无贴图版备份在会话 scratchpad（非仓库）。

| 事件 | 触发 | Runtime path | 减面后 | 说明 |
| --- | --- | --- | --- | --- |
| 冰鉴（冰棱簇） | `RESOURCE_USE` resourceType=`ICE_BOX` | `Assets/LycheeReplayDemo/ModelsGLB/event_icebox.glb` | 3772 tris / 270KB / 512贴图 | 冰蓝 tint；尖刺剪影 |
| 快马/短驿马（奔马） | `RESOURCE_USE` resourceType=`FAST_HORSE`/`SHORT_HORSE` | `Assets/LycheeReplayDemo/ModelsGLB/event_horse.glb` | 3506 tris / 254KB / 512贴图 | 金色 tint；快/短共用此模型，靠 tint+光柱+缩放区分 |
| 荔枝转坏（蔫荔枝串） | `GOOD_TO_BAD` | `Assets/LycheeReplayDemo/ModelsGLB/event_spoil.glb` | 3526 tris / 236KB / 512贴图 | 琥珀红 tint；圆串剪影 |

事件实际触发时点（map1 replay.txt）：round 81/177/184/202/204/207/294/477；开头不出现。

接线：编辑器脚本 `Editor/LycheeEventModelHookup_20260629.cs` 自动把三个 GLB 赋到
`LycheeReplayPlayer.eventModelIceBox/eventModelHorse/eventModelSpoil`（母版场景 + GeneratedMaps 克隆）。
