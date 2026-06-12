《一骑红尘：荔枝争运战》角色动画放置说明
==========================================
1. 把 Mixamo / AI 生成的动画 FBX 或 .anim 文件直接放进本目录
   （Assets/LycheeReplayDemo/Animations/）。
2. 文件名或动画片段名包含以下关键词（不区分大小写）即自动接入：
   walk / idle / row / attack / build / cheer
   例如：Captain_Walk.fbx、rowing_row_loop.anim、Attack01.fbx。
3. 放好后执行菜单 Tools > Lychee > Replay Upgrade 20260612 > Scan Animation Clips，
   片段会自动填入 Resources/LycheeAnimationLibrary.asset，
   并在工程根目录生成 LycheeAnimationLibrary_20260612_report.txt 报告。
4. 人形骨骼命名与 Captain 模型一致（Mixamo 标准：Hips/Spine/Spine1/Spine2/Neck/Head/
   Left(Right)Shoulder/Arm/ForeArm/Hand/UpLeg/Leg/Foot/ToeBase）可直接重定向；
   FBX 导入 Rig 选 Humanoid 或 Generic 均可，Legacy 片段会被跳过。
5. walk / idle / row 槽位由 LycheeCharacterMotion 用 Playables 循环播放
   （记得在导入设置里勾选 Loop Time）；缺的槽位继续使用程序化骨骼动画；
   attack / build / cheer 槽位当前保留，动作仍由程序化覆盖层实现。
