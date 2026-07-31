from __future__ import annotations

import json
import math
import random
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter


ROOT = Path(__file__).resolve().parents[1]
STREAMING = ROOT / "Assets" / "StreamingAssets"
UI_THEME = STREAMING / "UITheme"
EVENT_ICONS = UI_THEME / "EventIcons"
CARD_BORDERS = UI_THEME / "CardBorders"
VIDEO_DIR = STREAMING / "Video"
AUDIO_DIR = STREAMING / "Audio"


GOLD = "#f2c15b"
GOLD_DARK = "#9b6424"
CREAM = "#fff0b8"
INK = "#24151b"
RED = "#8f2430"
DEEP_RED = "#401820"
JADE = "#3b8f74"
BLUE = "#3f7799"
PURPLE = "#604985"
SILVER = "#cfe1df"


def ensure_dirs() -> None:
    for path in (EVENT_ICONS, CARD_BORDERS, VIDEO_DIR, AUDIO_DIR):
        path.mkdir(parents=True, exist_ok=True)


def write_text(path: Path, text: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(text.strip() + "\n", encoding="utf-8")


def icon_svg(title: str, slug: str, body: str, bg1: str = DEEP_RED, bg2: str = "#141016") -> str:
    return f"""<?xml version="1.0" encoding="UTF-8"?>
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 128 128" role="img" aria-labelledby="title-{slug}">
  <title id="title-{slug}">{title}</title>
  <defs>
    <radialGradient id="lacquer-{slug}" cx="42%" cy="30%" r="78%">
      <stop offset="0%" stop-color="{bg1}"/>
      <stop offset="58%" stop-color="{bg2}"/>
      <stop offset="100%" stop-color="#090609"/>
    </radialGradient>
    <linearGradient id="gold-{slug}" x1="14" y1="9" x2="114" y2="119" gradientUnits="userSpaceOnUse">
      <stop offset="0" stop-color="#fff3b0"/>
      <stop offset="0.45" stop-color="{GOLD}"/>
      <stop offset="1" stop-color="#70400f"/>
    </linearGradient>
    <linearGradient id="rim-{slug}" x1="24" y1="18" x2="104" y2="110" gradientUnits="userSpaceOnUse">
      <stop offset="0" stop-color="#65151e"/>
      <stop offset="0.55" stop-color="#2a1017"/>
      <stop offset="1" stop-color="#12080d"/>
    </linearGradient>
    <filter id="shadow-{slug}" x="-20%" y="-20%" width="140%" height="140%">
      <feDropShadow dx="0" dy="4" stdDeviation="2.4" flood-color="#000000" flood-opacity="0.52"/>
    </filter>
  </defs>
  <g filter="url(#shadow-{slug})">
    <path d="M64 8c10 0 16 8 24 10 10 3 19 12 22 22 2 8 10 14 10 24s-8 16-10 24c-3 10-12 19-22 22-8 2-14 10-24 10s-16-8-24-10c-10-3-19-12-22-22-2-8-10-14-10-24s8-16 10-24c3-10 12-19 22-22 8-2 14-10 24-10z" fill="url(#gold-{slug})"/>
    <path d="M64 15c9 0 14 7 21 9 9 2 16 10 19 19 2 7 9 12 9 21s-7 14-9 21c-3 9-10 17-19 19-7 2-12 9-21 9s-14-7-21-9c-9-2-16-10-19-19-2-7-9-12-9-21s7-14 9-21c3-9 10-17 19-19 7-2 12-9 21-9z" fill="url(#rim-{slug})" stroke="#ffe4a0" stroke-width="1.6" opacity="0.98"/>
    <circle cx="64" cy="64" r="43" fill="url(#lacquer-{slug})" stroke="url(#gold-{slug})" stroke-width="3.8"/>
    <circle cx="64" cy="64" r="35.5" fill="none" stroke="#fff0b8" stroke-width="1.1" opacity="0.26"/>
    <path d="M31 42c7-7 17-7 24 0M73 42c7-7 17-7 24 0M31 86c7 7 17 7 24 0M73 86c7 7 17 7 24 0" fill="none" stroke="#fff0b8" stroke-width="2.2" stroke-linecap="round" opacity="0.22"/>
    <path d="M30 63c8-9 20-9 28 0 8 9 20 9 28 0 4-4 8-6 13-6M29 72c8-7 18-7 26 0 7 7 18 7 26 0 5-4 10-6 16-5" fill="none" stroke="#f4c768" stroke-width="1.2" stroke-linecap="round" opacity="0.2"/>
  </g>
  <g filter="url(#shadow-{slug})">
{body}
  </g>
</svg>"""


def icons() -> dict[str, str]:
    return {
        "icon_event_contest_swords.svg": icon_svg(
            "抗争交叉剑",
            "contest-swords",
            f"""    <g stroke="{GOLD}" stroke-linecap="round" stroke-linejoin="round">
      <g transform="rotate(45 64 64)">
        <path d="M64 17l7 13-3 55-4 15-4-15-3-55z" fill="#e9edf1" stroke-width="3"/>
        <path d="M53 84h22" stroke-width="7"/>
        <path d="M64 91v20" stroke-width="6"/>
        <circle cx="64" cy="115" r="5" fill="{GOLD}" stroke="none"/>
      </g>
      <g transform="rotate(-45 64 64)">
        <path d="M64 17l7 13-3 55-4 15-4-15-3-55z" fill="#e9edf1" stroke-width="3"/>
        <path d="M53 84h22" stroke-width="7"/>
        <path d="M64 91v20" stroke-width="6"/>
        <circle cx="64" cy="115" r="5" fill="{GOLD}" stroke="none"/>
      </g>
      <circle cx="64" cy="64" r="8" fill="{RED}" stroke-width="3"/>
    </g>""",
        ),
        "icon_event_clear_obstacle.svg": icon_svg(
            "清障",
            "clear-obstacle",
            f"""    <g fill="none" stroke-linecap="round" stroke-linejoin="round">
      <path d="M28 82c14-4 60-4 72 0" stroke="{GOLD}" stroke-width="5"/>
      <path d="M37 72l16-16 12 12-16 16z" fill="#6b5748" stroke="{CREAM}" stroke-width="3"/>
      <path d="M55 55l35-24" stroke="{GOLD}" stroke-width="7"/>
      <path d="M84 27l12 12" stroke="{GOLD}" stroke-width="4"/>
      <path d="M28 92h19M58 94h18M86 92h14" stroke="#d9b169" stroke-width="4" opacity="0.75"/>
      <circle cx="35" cy="73" r="5" fill="#8f7b62" stroke="{GOLD}" stroke-width="2"/>
      <circle cx="90" cy="76" r="6" fill="#8f7b62" stroke="{GOLD}" stroke-width="2"/>
    </g>""",
        ),
        "icon_event_guard_set.svg": icon_svg(
            "设卡",
            "guard-set",
            f"""    <g stroke-linecap="round" stroke-linejoin="round">
      <path d="M32 81h64" stroke="{GOLD}" stroke-width="7"/>
      <path d="M37 48v48M91 48v48" stroke="{GOLD}" stroke-width="8"/>
      <path d="M31 69l66-25M31 47l66 24" stroke="#7b4326" stroke-width="11"/>
      <path d="M31 69l66-25M31 47l66 24" stroke="{GOLD}" stroke-width="3"/>
      <path d="M92 36l-1 20" stroke="{GOLD}" stroke-width="4"/>
      <path d="M94 38c8 2 12 6 15 12-6 1-11 0-17-4z" fill="{RED}" stroke="{GOLD}" stroke-width="2"/>
      <path d="M28 99h72" stroke="{CREAM}" stroke-width="4" opacity="0.7"/>
    </g>""",
        ),
        "icon_event_guard_break.svg": icon_svg(
            "破卡",
            "guard-break",
            f"""    <g fill="none" stroke-linecap="round" stroke-linejoin="round">
      <path d="M31 51l26 13M72 71l25 13" stroke="#7b4326" stroke-width="11"/>
      <path d="M31 75l24-15M76 49l22-13" stroke="#7b4326" stroke-width="11"/>
      <path d="M31 51l26 13M72 71l25 13M31 75l24-15M76 49l22-13" stroke="{GOLD}" stroke-width="3"/>
      <path d="M39 45v42M90 45v42" stroke="{GOLD}" stroke-width="7"/>
      <path d="M62 29l-8 27 15-4-8 33 23-43-16 5 7-18z" fill="#fff0a6" stroke="{GOLD_DARK}" stroke-width="3"/>
      <path d="M27 94h22M77 96h24" stroke="{CREAM}" stroke-width="4" opacity="0.72"/>
    </g>""",
        ),
        "icon_event_squad_dispatch.svg": icon_svg(
            "小分队出发",
            "squad-dispatch",
            f"""    <g stroke-linecap="round" stroke-linejoin="round">
      <circle cx="35" cy="58" r="7" fill="{CREAM}" stroke="{GOLD}" stroke-width="3"/>
      <circle cx="52" cy="51" r="7" fill="{CREAM}" stroke="{GOLD}" stroke-width="3"/>
      <circle cx="51" cy="73" r="7" fill="{CREAM}" stroke="{GOLD}" stroke-width="3"/>
      <path d="M28 79c10-8 25-9 36 0M43 94c16-10 39-12 58-6" fill="none" stroke="{GOLD}" stroke-width="5"/>
      <path d="M64 62h34" stroke="#fff0b8" stroke-width="8"/>
      <path d="M92 48l18 14-18 14" fill="none" stroke="#fff0b8" stroke-width="8"/>
      <path d="M24 100h30M29 39h21" stroke="{GOLD}" stroke-width="4" opacity="0.65"/>
    </g>""",
        ),
        "icon_event_squad_arrive.svg": icon_svg(
            "小分队抵达",
            "squad-arrive",
            f"""    <g stroke-linecap="round" stroke-linejoin="round">
      <path d="M63 24c-15 0-27 12-27 27 0 21 27 49 27 49s27-28 27-49c0-15-12-27-27-27z" fill="#5d2730" stroke="{GOLD}" stroke-width="5"/>
      <circle cx="63" cy="51" r="11" fill="{CREAM}" stroke="{GOLD_DARK}" stroke-width="3"/>
      <path d="M55 50l7 8 16-18" fill="none" stroke="{JADE}" stroke-width="7"/>
      <path d="M88 35v45" stroke="{GOLD}" stroke-width="4"/>
      <path d="M91 36c8 2 13 6 16 12-6 2-11 0-17-3z" fill="{RED}" stroke="{GOLD}" stroke-width="2"/>
      <path d="M35 101h57" stroke="{CREAM}" stroke-width="5" opacity="0.65"/>
    </g>""",
        ),
        "icon_event_squad_fail.svg": icon_svg(
            "小分队失败",
            "squad-fail",
            f"""    <g fill="none" stroke-linecap="round" stroke-linejoin="round">
      <path d="M46 28v72" stroke="{GOLD}" stroke-width="6"/>
      <path d="M50 31c17 3 24 9 36 5-1 12-5 21-17 25-8 3-14 0-21-3z" fill="#5f2028" stroke="{GOLD}" stroke-width="4"/>
      <path d="M59 42l16 12M75 42L59 54" stroke="{CREAM}" stroke-width="5"/>
      <path d="M78 75l24 24M102 75L78 99" stroke="#ffdf73" stroke-width="9"/>
      <path d="M30 101h40" stroke="{CREAM}" stroke-width="5" opacity="0.6"/>
    </g>""",
        ),
        "icon_event_resource_claim.svg": icon_svg(
            "资源领取",
            "resource-claim",
            f"""    <g stroke-linecap="round" stroke-linejoin="round">
      <path d="M35 52h58l-6 43H41z" fill="#6e3d22" stroke="{GOLD}" stroke-width="5"/>
      <path d="M35 52l12-17h34l12 17" fill="#8a5230" stroke="{GOLD}" stroke-width="4"/>
      <path d="M48 65h32M46 79h35" stroke="{GOLD}" stroke-width="3" opacity="0.75"/>
      <circle cx="52" cy="47" r="6" fill="#c94038" stroke="{CREAM}" stroke-width="2"/>
      <circle cx="64" cy="43" r="7" fill="#e34b43" stroke="{CREAM}" stroke-width="2"/>
      <circle cx="76" cy="47" r="6" fill="#c94038" stroke="{CREAM}" stroke-width="2"/>
      <path d="M78 29l9 9 17-19" fill="none" stroke="{JADE}" stroke-width="7"/>
      <path d="M31 101h66" stroke="{CREAM}" stroke-width="4" opacity="0.58"/>
    </g>""",
            "#234b37",
            "#101815",
        ),
        "icon_event_verify_gate.svg": icon_svg(
            "宫门验核",
            "verify-gate",
            f"""    <g stroke-linecap="round" stroke-linejoin="round">
      <path d="M31 55h66L85 42H43z" fill="{RED}" stroke="{GOLD}" stroke-width="5"/>
      <path d="M39 57v40M89 57v40M51 64v32M77 64v32" stroke="{GOLD}" stroke-width="6"/>
      <path d="M32 98h64" stroke="{GOLD}" stroke-width="6"/>
      <circle cx="82" cy="76" r="19" fill="#5b1f27" stroke="{CREAM}" stroke-width="4"/>
      <path d="M72 75l8 8 16-19" fill="none" stroke="{JADE}" stroke-width="7"/>
      <path d="M45 38h38" stroke="{CREAM}" stroke-width="4" opacity="0.72"/>
    </g>""",
            "#2c2239",
            "#120f19",
        ),
        "icon_event_forced_pass.svg": icon_svg(
            "强制通行",
            "forced-pass",
            f"""    <g fill="none" stroke-linecap="round" stroke-linejoin="round">
      <path d="M33 36h62M40 37v62M88 37v62" stroke="{GOLD}" stroke-width="7"/>
      <path d="M47 52h34M47 70h34" stroke="#704126" stroke-width="8"/>
      <path d="M24 70h55" stroke="{CREAM}" stroke-width="10"/>
      <path d="M72 49l31 21-31 21" stroke="{CREAM}" stroke-width="10"/>
      <path d="M55 49l-11 18 14-3-12 24" stroke="#ffdc6d" stroke-width="4"/>
      <path d="M30 101h69" stroke="{GOLD}" stroke-width="4" opacity="0.65"/>
    </g>""",
        ),
        "icon_event_delivery_success.svg": icon_svg(
            "交付成功",
            "delivery-success",
            f"""    <g stroke-linecap="round" stroke-linejoin="round">
      <path d="M33 58h62L84 45H44z" fill="{RED}" stroke="{GOLD}" stroke-width="4"/>
      <path d="M42 59v35M86 59v35M31 95h66" stroke="{GOLD}" stroke-width="5"/>
      <path d="M49 73h30l-4 22H53z" fill="#6e3d22" stroke="{GOLD}" stroke-width="4"/>
      <circle cx="58" cy="69" r="5" fill="#dc3d36" stroke="{CREAM}" stroke-width="2"/>
      <circle cx="67" cy="66" r="5" fill="#e74a41" stroke="{CREAM}" stroke-width="2"/>
      <circle cx="75" cy="70" r="5" fill="#c93b34" stroke="{CREAM}" stroke-width="2"/>
      <path d="M76 34l8 8 17-20" fill="none" stroke="{JADE}" stroke-width="7"/>
      <path d="M31 35l9 7M52 25l4 10M99 54l11 2" stroke="{CREAM}" stroke-width="4" opacity="0.72"/>
    </g>""",
            "#472218",
            "#140c0a",
        ),
        "icon_event_process_progress.svg": icon_svg(
            "处理进度",
            "process-progress",
            f"""    <g fill="none" stroke-linecap="round" stroke-linejoin="round">
      <path d="M40 31h49l-5 66H45z" fill="#5b2c1e" stroke="{GOLD}" stroke-width="5"/>
      <path d="M47 37c10 5 24 5 34 0M47 91c10-5 24-5 34 0" stroke="{CREAM}" stroke-width="3" opacity="0.62"/>
      <path d="M52 48h23M52 60h17M52 83h25" stroke="{GOLD}" stroke-width="4"/>
      <circle cx="67" cy="70" r="14" fill="#24151b" stroke="{CREAM}" stroke-width="4"/>
      <path d="M67 60v11l9 6" stroke="{GOLD}" stroke-width="4"/>
      <path d="M35 101h58" stroke="{CREAM}" stroke-width="4" opacity="0.58"/>
    </g>""",
            "#4a2730",
            "#170d12",
        ),
        "icon_event_rush_phase.svg": icon_svg(
            "宫宴冲刺",
            "rush-phase",
            f"""    <g stroke-linecap="round" stroke-linejoin="round">
      <path d="M34 74c18-10 35-12 55-11l8 15c-25 1-42 6-61 17z" fill="#7b3522" stroke="{GOLD}" stroke-width="5"/>
      <path d="M86 47c11 2 17 9 20 19-10 2-18-1-25-9z" fill="{RED}" stroke="{GOLD}" stroke-width="4"/>
      <path d="M83 47l-17 28" stroke="{GOLD}" stroke-width="6"/>
      <path d="M49 43c13-17 26-10 21 5-3 10-14 15-18 27 19-15 38-7 24 16" fill="none" stroke="#ffda69" stroke-width="6"/>
      <path d="M25 58h26M22 73h28M27 88h18" stroke="{CREAM}" stroke-width="4" opacity="0.72"/>
    </g>""",
            "#5e1a19",
            "#170909",
        ),
        "icon_event_action_invalid.svg": icon_svg(
            "行动无效",
            "action-invalid",
            f"""    <g fill="none" stroke-linecap="round" stroke-linejoin="round">
      <path d="M39 31h51l-5 65H44z" fill="#5b2c1e" stroke="{GOLD}" stroke-width="5"/>
      <path d="M49 47h27M49 61h22M49 75h28" stroke="{CREAM}" stroke-width="4" opacity="0.72"/>
      <path d="M35 36l58 58M93 36L35 94" stroke="#ffdf7b" stroke-width="9"/>
      <path d="M35 36l58 58M93 36L35 94" stroke="#8f2430" stroke-width="4"/>
      <path d="M33 101h62" stroke="{GOLD}" stroke-width="4" opacity="0.62"/>
    </g>""",
            "#45171d",
            "#14080b",
        ),
        "icon_event_freshness_drop.svg": icon_svg(
            "鲜度下降",
            "freshness-drop",
            f"""    <g stroke-linecap="round" stroke-linejoin="round">
      <path d="M62 33c9-12 21-12 29-5-6 10-17 14-29 9z" fill="{JADE}" stroke="{GOLD}" stroke-width="4"/>
      <path d="M46 47c9-12 29-13 39 1 10 13 5 35-12 45-11 7-28 3-35-9-8-13-2-26 8-37z" fill="#c83d35" stroke="{GOLD}" stroke-width="5"/>
      <circle cx="56" cy="62" r="5" fill="#ffb2a3" stroke="none" opacity="0.75"/>
      <path d="M89 47v38" stroke="{CREAM}" stroke-width="7"/>
      <path d="M76 75l13 15 13-15" fill="none" stroke="{CREAM}" stroke-width="7"/>
      <path d="M36 101h58" stroke="{GOLD}" stroke-width="4" opacity="0.62"/>
    </g>""",
            "#4a1719",
            "#100707",
        ),
        "icon_event_match_end.svg": icon_svg(
            "比赛结束",
            "match-end",
            f"""    <g stroke-linecap="round" stroke-linejoin="round">
      <path d="M32 50h64L84 36H44z" fill="{RED}" stroke="{GOLD}" stroke-width="5"/>
      <path d="M39 52v38M89 52v38M31 92h66" stroke="{GOLD}" stroke-width="6"/>
      <path d="M46 60h36v20H46z" fill="#2a1017" stroke="{CREAM}" stroke-width="3"/>
      <path d="M53 70h22" stroke="{GOLD}" stroke-width="4"/>
      <circle cx="89" cy="84" r="16" fill="#7a202a" stroke="{GOLD}" stroke-width="4"/>
      <path d="M80 84l7 7 13-17" fill="none" stroke="{CREAM}" stroke-width="6"/>
      <path d="M35 103h58" stroke="{CREAM}" stroke-width="4" opacity="0.62"/>
    </g>""",
            "#4a2215",
            "#120907",
        ),
        "icon_event_match_start.svg": icon_svg(
            "比赛开始",
            "match-start",
            f"""    <g stroke-linecap="round" stroke-linejoin="round">
      <path d="M43 29v72" stroke="{GOLD}" stroke-width="7"/>
      <path d="M47 33c22 3 32 12 49 7-1 18-8 29-24 34-10 3-17-1-25-5z" fill="{RED}" stroke="{GOLD}" stroke-width="5"/>
      <path d="M56 48h25M56 61h18" stroke="{CREAM}" stroke-width="4" opacity="0.75"/>
      <path d="M30 101h64" stroke="{CREAM}" stroke-width="5" opacity="0.62"/>
      <path d="M84 31l7-9M96 39l11-2M86 52l10 8" stroke="#ffdf7b" stroke-width="4"/>
    </g>""",
            "#4a151c",
            "#12080b",
        ),
        "icon_event_move_progress.svg": icon_svg(
            "移动进度",
            "move-progress",
            f"""    <g fill="none" stroke-linecap="round" stroke-linejoin="round">
      <path d="M27 82c18-14 40-16 72-10" stroke="{GOLD}" stroke-width="6"/>
      <path d="M48 54c9-16 29-17 39-4 8 11 9 25-3 36" stroke="{CREAM}" stroke-width="7"/>
      <path d="M62 45l-9 23 18-4-11 29 28-45-19 5 8-17z" fill="#ffdf7b" stroke="{GOLD_DARK}" stroke-width="3"/>
      <path d="M29 55h23M23 68h25M33 96h54" stroke="{CREAM}" stroke-width="4" opacity="0.65"/>
    </g>""",
            "#2b2631",
            "#100d12",
        ),
        "icon_event_node_enter.svg": icon_svg(
            "进入节点",
            "node-enter",
            f"""    <g stroke-linecap="round" stroke-linejoin="round">
      <path d="M35 52h58L82 39H46z" fill="{RED}" stroke="{GOLD}" stroke-width="5"/>
      <path d="M43 54v42M85 54v42M33 97h62" stroke="{GOLD}" stroke-width="6"/>
      <path d="M52 72h21" stroke="{CREAM}" stroke-width="8"/>
      <path d="M67 57l22 15-22 15" fill="none" stroke="{CREAM}" stroke-width="8"/>
      <path d="M37 103h55" stroke="{CREAM}" stroke-width="4" opacity="0.58"/>
    </g>""",
            "#3d1d22",
            "#12090c",
        ),
        "icon_event_cost.svg": icon_svg(
            "成本结算",
            "cost",
            f"""    <g stroke-linecap="round" stroke-linejoin="round">
      <circle cx="49" cy="67" r="18" fill="#8a5328" stroke="{GOLD}" stroke-width="5"/>
      <circle cx="75" cy="58" r="18" fill="#a9692d" stroke="{GOLD}" stroke-width="5"/>
      <path d="M75 47v22M64 58h22M49 56v22M39 67h20" stroke="{CREAM}" stroke-width="4"/>
      <path d="M78 78l16 16M96 78L80 94" stroke="#ffdf7b" stroke-width="7"/>
      <path d="M31 101h66" stroke="{CREAM}" stroke-width="4" opacity="0.58"/>
    </g>""",
            "#4a2b17",
            "#130b07",
        ),
        "icon_event_task.svg": icon_svg(
            "任务事件",
            "task",
            f"""    <g stroke-linecap="round" stroke-linejoin="round">
      <path d="M39 34h50v62H39z" fill="#8a6330" stroke="{GOLD}" stroke-width="5"/>
      <path d="M34 34h60M34 96h60" stroke="{CREAM}" stroke-width="7"/>
      <path d="M51 52h26M51 66h20" stroke="#3b1b16" stroke-width="4"/>
      <circle cx="82" cy="78" r="12" fill="#8f2430" stroke="{GOLD}" stroke-width="3"/>
      <path d="M76 78h12" stroke="{CREAM}" stroke-width="4"/>
    </g>""",
            "#4a3218",
            "#140d08",
        ),
        "icon_event_bounty.svg": icon_svg(
            "悬赏事件",
            "bounty",
            f"""    <g stroke-linecap="round" stroke-linejoin="round">
      <circle cx="64" cy="64" r="28" fill="#7c3b23" stroke="{GOLD}" stroke-width="6"/>
      <circle cx="64" cy="64" r="15" fill="#24151b" stroke="{CREAM}" stroke-width="4"/>
      <path d="M64 27v-9M64 110v-9M27 64h-9M110 64h-9" stroke="{GOLD}" stroke-width="5"/>
      <path d="M43 85l-12 12M85 43l12-12M43 43L31 31M85 85l12 12" stroke="{CREAM}" stroke-width="4" opacity="0.7"/>
      <path d="M55 64h18" stroke="{GOLD}" stroke-width="5"/>
    </g>""",
            "#4f2418",
            "#130905",
        ),
        "icon_event_weather.svg": icon_svg(
            "天气事件",
            "weather",
            f"""    <g fill="none" stroke-linecap="round" stroke-linejoin="round">
      <path d="M35 68c-5-18 14-32 30-22 5-12 25-8 25 7 11 1 17 10 14 19-3 10-12 15-23 13H49c-8 0-13-6-14-17z" fill="#244d60" stroke="{GOLD}" stroke-width="5"/>
      <path d="M52 90l-6 15M67 91l-6 15M82 90l-6 15" stroke="#9bd9f0" stroke-width="4"/>
      <path d="M83 29l8 11 13 1-9 9 3 13-12-6-11 7 2-13-10-8 13-2z" fill="#ffdf7b" stroke="{GOLD_DARK}" stroke-width="3"/>
    </g>""",
            "#213243",
            "#0c1118",
        ),
        "icon_event_player_status.svg": icon_svg(
            "玩家状态",
            "player-status",
            f"""    <g fill="none" stroke-linecap="round" stroke-linejoin="round">
      <circle cx="55" cy="49" r="13" fill="#5b2c1e" stroke="{GOLD}" stroke-width="5"/>
      <path d="M31 89c10-21 38-25 55-8" stroke="{GOLD}" stroke-width="8"/>
      <path d="M82 35l23 23M105 35L82 58" stroke="#ffdf7b" stroke-width="7"/>
      <path d="M30 101h60" stroke="{CREAM}" stroke-width="4" opacity="0.58"/>
    </g>""",
            "#3e1a22",
            "#10070b",
        ),
    }


def border_svg(title: str, slug: str, accent: str, dark: str, mark: str) -> str:
    return f"""<?xml version="1.0" encoding="UTF-8"?>
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 512 720" role="img" aria-labelledby="title-{slug}">
  <title id="title-{slug}">{title}</title>
  <defs>
    <linearGradient id="edge-{slug}" x1="28" y1="20" x2="484" y2="700" gradientUnits="userSpaceOnUse">
      <stop offset="0" stop-color="#fff4b8"/>
      <stop offset="0.18" stop-color="#e6b94f"/>
      <stop offset="0.54" stop-color="{accent}"/>
      <stop offset="1" stop-color="#71430e"/>
    </linearGradient>
    <linearGradient id="lacquer-{slug}" x1="48" y1="38" x2="464" y2="682" gradientUnits="userSpaceOnUse">
      <stop offset="0" stop-color="#6a1720"/>
      <stop offset="0.52" stop-color="{dark}"/>
      <stop offset="1" stop-color="#15080d"/>
    </linearGradient>
    <filter id="soft-{slug}" x="-8%" y="-8%" width="116%" height="116%">
      <feDropShadow dx="0" dy="8" stdDeviation="8" flood-color="#000000" flood-opacity="0.38"/>
    </filter>
  </defs>
  <g fill="none" filter="url(#soft-{slug})" stroke-linecap="round" stroke-linejoin="round">
    <rect x="20" y="20" width="472" height="680" rx="28" stroke="url(#lacquer-{slug})" stroke-width="24"/>
    <rect x="29" y="29" width="454" height="662" rx="22" stroke="url(#edge-{slug})" stroke-width="9"/>
    <rect x="48" y="52" width="416" height="616" rx="12" stroke="#0f080b" stroke-width="12" opacity="0.9"/>
    <rect x="55" y="59" width="402" height="602" rx="8" stroke="url(#edge-{slug})" stroke-width="4"/>
    <path d="M88 72h120M304 72h120M88 648h120M304 648h120" stroke="#fff0b8" stroke-width="3.5" opacity="0.7"/>
    <path d="M72 112c38-8 64-30 78-62M440 112c-38-8-64-30-78-62M72 608c38 8 64 30 78 62M440 608c-38 8-64 30-78 62" stroke="url(#edge-{slug})" stroke-width="8"/>
    <path d="M73 146c24-22 48-22 72 0M367 146c24-22 48-22 72 0M73 574c24 22 48 22 72 0M367 574c24 22 48 22 72 0" stroke="#f6d47c" stroke-width="3" opacity="0.58"/>
    <path d="M73 196v328M439 196v328" stroke="#fff0b8" stroke-width="2" opacity="0.22"/>
    <path d="M99 112c18 6 30 6 48 0M365 112c18 6 30 6 48 0M99 608c18-6 30-6 48 0M365 608c18-6 30-6 48 0" stroke="{accent}" stroke-width="3" opacity="0.72"/>
  </g>
  <g transform="translate(206 38)" fill="{dark}" stroke="url(#edge-{slug})" stroke-width="8" stroke-linecap="round" stroke-linejoin="round">
    <path d="M50 0c20 0 32 16 32 34s-12 34-32 34-32-16-32-34S30 0 50 0z" fill="url(#lacquer-{slug})"/>
  </g>
  <g transform="translate(206 44)" fill="{dark}" stroke="#fff0b8" stroke-width="5.2" stroke-linecap="round" stroke-linejoin="round">
{mark}
  </g>
</svg>"""


def borders() -> dict[str, str]:
    return {
        "border_event_battle.svg": border_svg(
            "战斗事件卡边框",
            "battle",
            "#dc3d35",
            "#561923",
            """    <path d="M18 6l26 44M70 6L44 50"/>
    <path d="M10 56h68"/>""",
        ),
        "border_event_resource.svg": border_svg(
            "资源事件卡边框",
            "resource",
            "#46a06f",
            "#1f4e39",
            """    <path d="M16 28h56l-7 38H23z"/>
    <path d="M20 28l10-16h28l10 16"/>
    <circle cx="34" cy="24" r="5" fill="#46a06f"/>
    <circle cx="47" cy="20" r="6" fill="#46a06f"/>""",
        ),
        "border_event_weather.svg": border_svg(
            "天气事件卡边框",
            "weather",
            "#65b6d4",
            "#244d60",
            """    <path d="M18 47c-5-20 15-36 34-25 7-13 28-6 28 10 12 1 19 10 16 20-3 11-14 16-26 14H34c-9 0-14-7-16-19z"/>
    <path d="M32 78l-8 20M52 78l-8 20M72 78l-8 20"/>""",
        ),
        "border_event_squad.svg": border_svg(
            "小分队事件卡边框",
            "squad",
            "#4cae9b",
            "#1f544e",
            """    <circle cx="22" cy="34" r="10"/>
    <circle cx="46" cy="26" r="10"/>
    <circle cx="68" cy="38" r="10"/>
    <path d="M12 68c21-16 54-16 76 0"/>""",
        ),
        "border_event_verification.svg": border_svg(
            "验核事件卡边框",
            "verification",
            "#b69ad9",
            "#49305f",
            """    <path d="M16 42h64L68 28H28z"/>
    <path d="M24 44v34M72 44v34M16 80h64"/>
    <path d="M58 58l10 10 20-24"/>""",
        ),
    }


def event_icon_mapping() -> dict[str, object]:
    p = "UITheme/EventIcons/"
    return {
        "style": "tang_ui_vector",
        "basePath": "UITheme/EventIcons",
        "defaultIcon": p + "icon_event_process_progress.svg",
        "events": {
            "MATCH_START": p + "icon_event_match_start.svg",
            "ROUND_STATE": p + "icon_event_process_progress.svg",
            "MOVE_PROGRESS": p + "icon_event_move_progress.svg",
            "NODE_ENTER": p + "icon_event_node_enter.svg",
            "PROCESS_START": p + "icon_event_process_progress.svg",
            "PROCESS_INTERRUPT": p + "icon_event_process_progress.svg",
            "PROCESS_COMPLETE": p + "icon_event_process_progress.svg",
            "COST_FREEZE": p + "icon_event_cost.svg",
            "COST_COMMIT": p + "icon_event_cost.svg",
            "COST_BANKRUPT": p + "icon_event_cost.svg",
            "FRESHNESS_DROP": p + "icon_event_freshness_drop.svg",
            "GOOD_TO_BAD": p + "icon_event_freshness_drop.svg",
            "RESOURCE_CLAIM": p + "icon_event_resource_claim.svg",
            "GUARD_SET": p + "icon_event_guard_set.svg",
            "GUARD_REPLACE": p + "icon_event_guard_set.svg",
            "GUARD_DECAY": p + "icon_event_guard_set.svg",
            "GUARD_BREAK": p + "icon_event_guard_break.svg",
            "FORCED_PASS_START": p + "icon_event_forced_pass.svg",
            "FORCED_PASS_END": p + "icon_event_forced_pass.svg",
            "WINDOW_CONTEST_START": p + "icon_event_contest_swords.svg",
            "WINDOW_CARD_REVEAL": p + "icon_event_contest_swords.svg",
            "WINDOW_CARD_WIN": p + "icon_event_contest_swords.svg",
            "WINDOW_CARD_DRAW": p + "icon_event_contest_swords.svg",
            "WINDOW_CONTEST_END": p + "icon_event_contest_swords.svg",
            "SQUAD_DISPATCH": p + "icon_event_squad_dispatch.svg",
            "SQUAD_ARRIVE": p + "icon_event_squad_arrive.svg",
            "SQUAD_REINFORCE_SUCCESS": p + "icon_event_squad_arrive.svg",
            "SQUAD_WEAKEN_SUCCESS": p + "icon_event_squad_arrive.svg",
            "SQUAD_CLEAR_SUCCESS": p + "icon_event_clear_obstacle.svg",
            "SQUAD_SCOUT_SUCCESS": p + "icon_event_squad_arrive.svg",
            "SQUAD_FAIL": p + "icon_event_squad_fail.svg",
            "GUARD_SKIRMISH_VISUAL": p + "icon_event_contest_swords.svg",
            "TASK_SPAWN": p + "icon_event_task.svg",
            "TASK_COMPLETE": p + "icon_event_task.svg",
            "BOUNTY_SPAWN": p + "icon_event_bounty.svg",
            "BOUNTY_COMPLETE": p + "icon_event_bounty.svg",
            "RUSH_PHASE_START": p + "icon_event_rush_phase.svg",
            "VERIFY_GATE_START": p + "icon_event_verify_gate.svg",
            "VERIFY_GATE_COMPLETE": p + "icon_event_verify_gate.svg",
            "DELIVER_SUCCESS": p + "icon_event_delivery_success.svg",
            "PLAYER_DISCONNECT": p + "icon_event_player_status.svg",
            "PLAYER_FORFEIT": p + "icon_event_player_status.svg",
            "MATCH_END": p + "icon_event_match_end.svg",
            "ACTION_REJECTED": p + "icon_event_action_invalid.svg",
            "WEATHER_FORECAST": p + "icon_event_weather.svg",
            "WEATHER_START": p + "icon_event_weather.svg",
            "WEATHER_END": p + "icon_event_weather.svg",
        },
        "errorCodes": {
            "INVALID_JSON": p + "icon_event_action_invalid.svg",
            "INVALID_LENGTH_PREFIX": p + "icon_event_action_invalid.svg",
            "INVALID_PLAYER": p + "icon_event_action_invalid.svg",
            "INVALID_ROUND": p + "icon_event_action_invalid.svg",
            "INVALID_ACTION_TYPE": p + "icon_event_action_invalid.svg",
            "INVALID_ACTION_CONFLICT": p + "icon_event_action_invalid.svg",
            "DELIVER_NOT_VERIFIED": p + "icon_event_action_invalid.svg",
            "RUSH_TACTIC_INVALID_BINDING": p + "icon_event_action_invalid.svg",
        },
    }


def write_event_icon_mapping() -> None:
    text = json.dumps(event_icon_mapping(), ensure_ascii=False, indent=2)
    write_text(UI_THEME / "event_icon_mapping.json", text)


def draw_rain(path: Path) -> None:
    scale = 4
    size = 256
    img = Image.new("RGBA", (size * scale, size * scale), (0, 0, 0, 0))
    draw = ImageDraw.Draw(img, "RGBA")
    rng = random.Random(1047)

    for i in range(7):
        x = rng.randint(70, 190) * scale
        y = rng.randint(18, 92) * scale
        length = rng.randint(100, 170) * scale
        dx = rng.randint(34, 58) * scale
        alpha = rng.randint(42, 88)
        draw.line((x, y, x + dx, y + length), fill=(136, 210, 245, alpha), width=rng.randint(3, 5) * scale)

    draw.line((112 * scale, 26 * scale, 151 * scale, 222 * scale), fill=(170, 230, 255, 190), width=11 * scale)
    draw.line((116 * scale, 34 * scale, 151 * scale, 218 * scale), fill=(244, 252, 255, 225), width=4 * scale)
    draw.ellipse((139 * scale, 208 * scale, 158 * scale, 232 * scale), fill=(180, 232, 255, 170))
    img = img.filter(ImageFilter.GaussianBlur(0.45 * scale))
    img = img.resize((size, size), Image.Resampling.LANCZOS)
    img.save(path)


def draw_fog(path: Path) -> None:
    scale = 4
    size = 256
    img = Image.new("RGBA", (size * scale, size * scale), (0, 0, 0, 0))
    alpha = Image.new("L", (size * scale, size * scale), 0)
    ad = ImageDraw.Draw(alpha)
    rng = random.Random(2077)

    for _ in range(18):
        cx = rng.randint(45, 210) * scale
        cy = rng.randint(64, 184) * scale
        rx = rng.randint(28, 74) * scale
        ry = rng.randint(12, 36) * scale
        a = rng.randint(28, 70)
        ad.ellipse((cx - rx, cy - ry, cx + rx, cy + ry), fill=a)

    for y, a in ((102, 92), (132, 118), (160, 78)):
        ad.rounded_rectangle((28 * scale, (y - 14) * scale, 228 * scale, (y + 14) * scale), radius=14 * scale, fill=a)

    alpha = alpha.filter(ImageFilter.GaussianBlur(9 * scale))
    color = Image.new("RGBA", (size * scale, size * scale), (205, 221, 218, 0))
    color.putalpha(alpha)
    img.alpha_composite(color)
    img = img.resize((size, size), Image.Resampling.LANCZOS)
    img.save(path)


def draw_heat(path: Path) -> None:
    scale = 4
    size = 256
    img = Image.new("RGBA", (size * scale, size * scale), (0, 0, 0, 0))
    draw = ImageDraw.Draw(img, "RGBA")

    for i, base_x in enumerate((82, 108, 134, 160)):
        points = []
        for y in range(24, 232, 5):
            phase = i * 1.2
            x = base_x + math.sin(y / 18.0 + phase) * (9 + i * 1.5)
            points.append((x * scale, y * scale))
        alpha = 82 + i * 24
        width = (5 if i in (1, 2) else 4) * scale
        draw.line(points, fill=(255, 186, 64, alpha), width=width, joint="curve")
        draw.line(points, fill=(255, 244, 173, min(210, alpha + 52)), width=max(1, width // 3), joint="curve")

    img = img.filter(ImageFilter.GaussianBlur(1.15 * scale))
    img = img.resize((size, size), Image.Resampling.LANCZOS)
    img.save(path)


def write_video_prompts() -> None:
    text = """
# 短动画生成提示词

通用规格：H.264 MP4，1920x1080，30fps，4-6 秒；写实偏风格化 3D 游戏演出；唐代驿道、朱红鎏金 UI 气质；无文字、无字幕、无水印、无现代物件。镜头要给中景动作和结尾定格，便于 Unity 叠 UI。

## clear_obstacle.mp4 / 清障

English prompt: A Tang dynasty courier squad clears a blocked mountain road, chopping tangled branches and pushing away fallen stones from a narrow official route. Stylized cinematic 3D game animation, warm dusk light, dust puffs, fast readable action, camera starts low beside the road then pans to reveal the route opening. End with the road visibly clear and a soft gold highlight on the path. No text, no logos, no gore, no modern tools.

## break_guard.mp4 / 破卡

English prompt: A courier vanguard breaks through a wooden barricade checkpoint on an ancient Chinese road, splintered rails flying outward, red and blue team pennants fluttering, dramatic but non-violent impact. Stylized 3D game cut-in, bronze and crimson palette, short burst of dust and wood fragments, camera pushes in then snaps to a clear opened passage. No text, no logos, no blood, no modern objects.

## squad_dispatch.mp4 / 小分队派出

English prompt: A small Tang courier squad departs from a relay station, three riders and foot guards moving out with a command flag, horse hooves kicking dust, urgent but disciplined. Stylized 3D game animation, side-to-three-quarter camera, golden morning light, visible route line energy sweeping forward. End on the squad moving toward the horizon. No text, no logos, no modern objects.

## resource_contest.mp4 / 资源争夺

English prompt: Two courier teams converge on a relay station supply cache, reaching for ice boxes, pass tokens, and sealed documents on a wooden table, tense competitive motion without direct violence. Tang dynasty station courtyard, stylized 3D game animation, red team and blue team color accents, quick close-up of hands and supplies, then a pullback to show one team securing the resource. No text, no logos, no modern items.

## gate_verification.mp4 / 宫门验核

English prompt: At Zhuque Gate in Tang Chang'an, officials inspect a pass document and stamp it with a vermilion seal as a courier cart waits before the palace gate. Stylized cinematic 3D game animation, solemn gold-and-red lighting, close-up of document and seal, bell-like visual pulse, then gate doors opening slightly. No readable text, no logos, no modern objects.

## weather_start.mp4 / 天气开始

English prompt: A tactical map of ancient Chinese relay routes is overtaken by changing weather: rain streaks sweep across water routes, mountain fog rolls over passes, and a heat shimmer rises over the plains. Stylized 3D game transition, clear particle effects, gold route lines remain visible, camera glides above the map and settles on the affected region. No text, no logos, no modern objects.
"""
    write_text(VIDEO_DIR / "video_generation_prompts.md", text)


def write_audio_prompts() -> None:
    text = """
# 音频生成提示词

说明：这里不放空音频占位文件，避免 Unity 误加载无效资源。以下提示词可用于后续生成实际 `.ogg` / `.wav`，文件名与素材清单保持一致。

## BGM

- `bgm_normal.ogg`: Seamless loop, 3-5 minutes, Tang dynasty inspired instrumental game background music, medium tempo, pipa, guzheng, bamboo flute, soft frame drum, restrained strategic mood, no vocals, no modern synth lead.
- `bgm_rush.ogg`: Seamless loop, 90-150 seconds, urgent palace banquet rush phase music, faster war drums, plucked strings, low bronze percussion, rising tension, no vocals, no modern electronic beat.
- `bgm_settlement.ogg`: Seamless loop or clean ending, 60-120 seconds, grand Tang palace settlement music, bianzhong bells, large drums, warm strings, ceremonial victory mood, no vocals.

## SFX

- `sfx_card_reveal.ogg`: Short 0.6s card flip reveal, silk snap plus small bronze chime.
- `sfx_contest_win.ogg`: Short 1.2s contest win accent, drum hit plus bright gong tail.
- `sfx_guard_set.ogg`: 1.2s wooden barricade built, stakes thud into earth, rope pull, low drum.
- `sfx_guard_break.ogg`: 1.3s wooden barricade splinter break, dust impact, no explosion.
- `sfx_squad_dispatch.ogg`: 1.5s squad departure, horse snort, hooves starting, short command shout without intelligible words.
- `sfx_squad_arrive.ogg`: 1.2s squad arrival, hooves stop, small flag flutter, confirming chime.
- `sfx_task_spawn.ogg`: 1.0s imperial notice appears, scroll unfurl, wooden clapper, small gong.
- `sfx_task_complete.ogg`: 1.0s task complete, seal stamp, coin sparkle, warm chime.
- `sfx_verify_gate.ogg`: 1.5s palace gate verification, official seal stamp, deep bell resonance.
- `sfx_deliver.ogg`: 1.8s successful delivery, bianzhong flourish, crowd cheer kept subtle.
- `sfx_rush_horn.ogg`: 1.6s rush phase horn, ancient brass horn and war drum roll.
- `sfx_fresh_warning.ogg`: 0.8s freshness warning, tense wooden clack and muted gong.
- `sfx_ui_click.ogg`: 0.15s soft bronze UI click.
- `sfx_weather_rain.ogg`: Seamless loop, heavy rain ambience, water route rain, distant thunder very subtle.
- `sfx_weather_wind.ogg`: Seamless loop, mountain fog wind, cloth flutter, low airy gusts.
- `sfx_weather_heat.ogg`: Seamless loop, hot summer cicadas, dry wind, restrained and non-intrusive.
- `sfx_hoofbeats.ogg`: Seamless loop, horse hooves on packed earth road, medium speed.
- `sfx_water_row.ogg`: Seamless loop, oars rowing and small boat water movement.
"""
    write_text(AUDIO_DIR / "audio_generation_prompts.md", text)


def write_manifest() -> None:
    text = """
# 缺口素材生成清单

风格说明：本批 SVG 是“唐风 UI 矢量版”，用于回放事件日志、地图提示、事件卡分类框等清晰识别场景。视觉关键词为朱红漆、鎏金、墨黑内盘、绢黄高光、云纹/如意纹装饰；它们不是手绘卡牌立绘，也不替代 420x600 卡面主插画。

## 事件图标 SVG

- `UITheme/EventIcons/icon_event_match_start.svg`: 比赛开始
- `UITheme/EventIcons/icon_event_move_progress.svg`: 移动进度
- `UITheme/EventIcons/icon_event_node_enter.svg`: 进入节点
- `UITheme/EventIcons/icon_event_process_progress.svg`: 处理进度 / 漏刻读条
- `UITheme/EventIcons/icon_event_cost.svg`: 成本冻结 / 成本提交 / 成本破产
- `UITheme/EventIcons/icon_event_freshness_drop.svg`: 鲜度下降 / 好果转坏果
- `UITheme/EventIcons/icon_event_contest_swords.svg`: 抗争交叉剑 / 窗口争夺
- `UITheme/EventIcons/icon_event_clear_obstacle.svg`: 清障
- `UITheme/EventIcons/icon_event_guard_set.svg`: 设卡
- `UITheme/EventIcons/icon_event_guard_break.svg`: 破卡
- `UITheme/EventIcons/icon_event_squad_dispatch.svg`: 小分队出发
- `UITheme/EventIcons/icon_event_squad_arrive.svg`: 小分队抵达
- `UITheme/EventIcons/icon_event_squad_fail.svg`: 小分队失败
- `UITheme/EventIcons/icon_event_resource_claim.svg`: 资源领取
- `UITheme/EventIcons/icon_event_verify_gate.svg`: 宫门验核
- `UITheme/EventIcons/icon_event_forced_pass.svg`: 强制通行
- `UITheme/EventIcons/icon_event_delivery_success.svg`: 交付成功
- `UITheme/EventIcons/icon_event_rush_phase.svg`: 宫宴冲刺
- `UITheme/EventIcons/icon_event_action_invalid.svg`: 行动无效 / 服务端驳回
- `UITheme/EventIcons/icon_event_match_end.svg`: 比赛结束
- `UITheme/EventIcons/icon_event_task.svg`: 皇榜任务事件
- `UITheme/EventIcons/icon_event_bounty.svg`: 悬赏事件
- `UITheme/EventIcons/icon_event_weather.svg`: 天气事件
- `UITheme/EventIcons/icon_event_player_status.svg`: 玩家掉线 / 弃赛

## 天气粒子 PNG

- `UITheme/weather_rain.png`: 256x256，透明底，斜向雨线粒子
- `UITheme/weather_fog.png`: 256x256，透明底，软雾团粒子
- `UITheme/weather_heat.png`: 256x256，透明底，热浪扭曲粒子

## 事件卡边框 SVG

- `UITheme/CardBorders/border_event_battle.svg`: 战斗 / 对抗
- `UITheme/CardBorders/border_event_resource.svg`: 资源
- `UITheme/CardBorders/border_event_weather.svg`: 天气
- `UITheme/CardBorders/border_event_squad.svg`: 小分队
- `UITheme/CardBorders/border_event_verification.svg`: 验核 / 宫门

## 提示词文档

- `Video/video_generation_prompts.md`: 清障、破卡、小分队派出、资源争夺、宫门验核、天气开始
- `Audio/audio_generation_prompts.md`: BGM/SFX 生成提示词，未生成假音频文件

## 事件图标映射

- `UITheme/event_icon_mapping.json`: 覆盖固定事件类型、回放样例中的 `ACTION_REJECTED` / `WEATHER_*`，以及常见 invalid errorCode。
"""
    write_text(STREAMING / "generated_missing_assets_manifest.md", text)


def main() -> None:
    ensure_dirs()

    for name, svg in icons().items():
        write_text(EVENT_ICONS / name, svg)

    for name, svg in borders().items():
        write_text(CARD_BORDERS / name, svg)

    draw_rain(UI_THEME / "weather_rain.png")
    draw_fog(UI_THEME / "weather_fog.png")
    draw_heat(UI_THEME / "weather_heat.png")
    write_event_icon_mapping()
    write_video_prompts()
    write_audio_prompts()
    write_manifest()


if __name__ == "__main__":
    main()
