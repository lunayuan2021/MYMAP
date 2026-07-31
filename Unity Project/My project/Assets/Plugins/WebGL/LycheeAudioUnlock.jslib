mergeInto(LibraryManager.library, {
  LycheeResumeAudioContext: function () {
    try {
      var ctx = null;
      if (typeof WEBAudio !== "undefined" && WEBAudio && WEBAudio.audioContext) {
        ctx = WEBAudio.audioContext;
      } else if (typeof Module !== "undefined" && Module) {
        ctx = Module.webAudioContext || Module.audioContext || Module.ctx || null;
      }
      if (ctx && ctx.state === "suspended" && typeof ctx.resume === "function") {
        ctx.resume();
      }
    } catch (e) {
    }
  },
  // 返回 1 表示 AudioContext 已真正 running（用户手势 + resume 完成后才会到）。
  // C# 端用它判定"解锁了没"，绕开已切到 Input System 后会抛异常的旧 UnityEngine.Input。
  LycheeAudioContextRunning: function () {
    try {
      var ctx = null;
      if (typeof WEBAudio !== "undefined" && WEBAudio && WEBAudio.audioContext) {
        ctx = WEBAudio.audioContext;
      } else if (typeof Module !== "undefined" && Module) {
        ctx = Module.webAudioContext || Module.audioContext || Module.ctx || null;
      }
      return (ctx && ctx.state === "running") ? 1 : 0;
    } catch (e) {
      return 0;
    }
  },
  // 回放首帧就绪时由 C#(LycheeReplayPlayer) 调用，掀掉开场封面——替代原来固定 2.6s 定时，
  // 慢机上 2.6s 时回放常还没开播，会过早露出腿不动、原地滑的冻结场景。
  LycheeHideSplash: function () {
    try {
      if (typeof window !== "undefined" && window.__lycheeHideSplash) {
        window.__lycheeHideSplash();
      }
    } catch (e) {
    }
  }
});
