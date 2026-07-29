(function () {
  if (typeof Plyr === "undefined") return;

  var commonOptions = {
    controls: [
      "play-large",
      "play",
      "progress",
      "current-time",
      "mute",
      "volume",
      "fullscreen"
    ],
    hideControls: true,
    resetOnEnd: false,
    keyboard: { focused: true, global: false },
    storage: { enabled: false },
    ratio: null
  };

  function applyAspect(el, player) {
    var w = el.videoWidth;
    var h = el.videoHeight;
    if (!w || !h) return;

    var portrait = h > w;
    el.classList.toggle("is-portrait", portrait);
    var root = el.closest(".plyr") || el.parentElement;
    if (root) root.classList.toggle("is-portrait", portrait);

    // Keep Plyr's box matching the real video ratio (e.g. 9:16 mobile clips).
    var ratio = w + ":" + h;
    try {
      player.ratio = ratio;
    } catch (_) {
      /* ignore older plyr builds */
    }
  }

  document.querySelectorAll("video.js-plyr").forEach(function (el) {
    if (el.dataset.plyrInit === "1") return;
    el.dataset.plyrInit = "1";

    var player = new Plyr(el, commonOptions);

    function syncRatio() {
      applyAspect(el, player);
    }

    if (el.readyState >= 1) {
      syncRatio();
    } else {
      el.addEventListener("loadedmetadata", syncRatio, { once: true });
    }
  });
})();
