using UnityEngine;

/// <summary>
/// 帧节奏：开启垂直同步 + 设 60 帧上限，让每帧时间稳定，使回放行进「丝滑」。
/// 原因：本工程默认 vSyncCount=0 且未设 targetFrameRate，帧节奏不稳→每帧 Time.deltaTime 忽大忽小，
/// 即便位置插值用了 Catmull-Rom+SmoothDamp 也会显得一顿一顿。
/// 纯渲染节奏设置，不改动回放数据/加载/启动路径。BeforeSceneLoad 自举、对编辑器播放与所有构建生效。
/// </summary>
public static class LycheeFrameRatePacing
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Apply()
    {
        // 跟随显示器刷新呈现，帧间隔稳定（原生包/有显示器环境的主力手段）
        QualitySettings.vSyncCount = 1;
        // WebGL / 无 vSync 环境的兜底上限；与 vSync 并存时由 vSync 主导
        Application.targetFrameRate = 60;
    }
}
