using UnityEngine;

/// <summary>
/// 小队"当前在水面上"的共享状态：LycheeReplayPlayer 每帧按河面采样写入，
/// LycheeCharacterMotion 读取后抑制步行摆动（船上是站立随波，不是水里行军）。
/// </summary>
public sealed class LycheeSquadWaterState : MonoBehaviour
{
    public bool onWater;
}
