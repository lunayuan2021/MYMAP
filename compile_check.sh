#!/bin/zsh
# 离线编译自检：模拟 Assembly-CSharp（运行时脚本）编译，Unity 不在前台也能验证语法/类型错误。
# 用法: ./compile_check.sh            → 编译 Assets 下全部运行时脚本
#       ./compile_check.sh editor    → 额外编译 Editor 脚本（引用 UnityEditor.dll）
UNITY="/Users/lunayuan/Documents/游戏引擎/6000.4.10f1/Unity.app/Contents"
PROJ="/Users/lunayuan/Documents/Unity Project/My project"
SA="$PROJ/Library/ScriptAssemblies"

REFS=()
for d in "$UNITY/Resources/Scripting/Managed/UnityEngine"/*.dll; do REFS+=("-r:$d"); done
REFS+=("-r:$UNITY/Resources/Scripting/NetStandard/ref/2.1.0/netstandard.dll")
for n in UnityEngine.UI Unity.RenderPipelines.Universal.Runtime Unity.RenderPipelines.Core.Runtime Unity.TextMeshPro Unity.InputSystem Unity.Timeline; do
  [ -f "$SA/$n.dll" ] && REFS+=("-r:$SA/$n.dll")
done

SRC=()
while IFS= read -r f; do SRC+=("$f"); done < <(find "$PROJ/Assets" -name "*.cs" -not -path "*/Editor/*" -not -path "*/TutorialInfo/*" -not -path "*Hill Rock*" -not -path "*Tree_Packs*")

if [ "$1" = "editor" ]; then
  # 注意：Managed/UnityEngine/ 目录已含 UnityEditor.*Module.dll（MenuItem 等都在 CoreModule），
  # 不要再引用 UnityEditor.dll 门面，否则 CS0433 类型重复。
  for n in Unity.RenderPipelines.Universal.Editor Unity.RenderPipelines.Core.Editor; do
    [ -f "$SA/$n.dll" ] && REFS+=("-r:$SA/$n.dll")
  done
  while IFS= read -r f; do SRC+=("$f"); done < <(find "$PROJ/Assets" -name "*.cs" -path "*/Editor/*" -not -path "*/TutorialInfo/*")
fi

OUT="${LYCHEE_OUT:-/tmp/lychee_compile_check.dll}"
"$UNITY/Resources/Scripting/NetCoreRuntime/dotnet" "$UNITY/Resources/Scripting/DotNetSdkRoslyn/csc.dll" \
  -nologo -target:library -nowarn:0169,0649,0414 -define:UNITY_EDITOR -define:ENABLE_INPUT_SYSTEM -langversion:9.0 \
  -out:"$OUT" "${REFS[@]}" "${SRC[@]}" 2>&1 | grep -E "error|Error" | head -60
RESULT=${pipestatus[1]}
if [ $RESULT -eq 0 ]; then echo "COMPILE_OK"; else echo "COMPILE_FAILED($RESULT)"; fi
