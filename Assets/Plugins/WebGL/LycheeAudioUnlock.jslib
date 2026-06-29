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
  }
});
