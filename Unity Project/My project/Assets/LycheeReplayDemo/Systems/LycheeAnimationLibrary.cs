using UnityEngine;

/// <summary>
/// 动画片段库（运行时数据容器）：
/// 由编辑器扫描器（LycheeAnimationLibraryBuilder_20260612）扫描 Assets/LycheeReplayDemo/Animations/
/// 下的 FBX/anim 资产，按文件名/片段名关键词填充，并存放在 Assets/LycheeReplayDemo/Resources/ 下。
/// LycheeCharacterMotion 在运行时通过 Instance 读取：walk/idle/row 槽位有非 legacy 片段时
/// 改用 Playables 播放，缺的槽位继续走程序化骨骼动画。
/// </summary>
public class LycheeAnimationLibrary : ScriptableObject
{
    public AnimationClip walk;
    public AnimationClip idle;
    public AnimationClip row;
    public AnimationClip attack;
    public AnimationClip build;
    public AnimationClip cheer;

    static LycheeAnimationLibrary cached;
    static bool searched;

    /// <summary>从 Resources 加载（带缓存）；资产不存在时返回 null。</summary>
    public static LycheeAnimationLibrary Instance
    {
        get
        {
            if (cached == null && !searched)
            {
                searched = true;
                cached = Resources.Load<LycheeAnimationLibrary>("LycheeAnimationLibrary");
            }
            return cached;
        }
    }
}
