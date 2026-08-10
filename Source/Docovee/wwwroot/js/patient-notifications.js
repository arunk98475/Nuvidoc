/**
 * Real-time patient booking notifications (SignalR).
 * Updates the bell badge without a full page refresh.
 */
(function (window) {
  "use strict";

  if (window.NuvidocPatientPush) return;

  var HUB_PATH = "/hubs/patient-notifications";
  var EVENT = "bookingUpdated";
  var connection = null;
  var starting = null;
  var joinedSession = null;
  var joinPatient = false;
  var listeners = [];
  var seenKeys = Object.create(null);
  var seenQueue = [];

  function messageKey(message) {
    if (!message) return "";
    if (message.notificationId != null) return "n:" + message.notificationId;
    if (message.conversationId) return "c:" + message.conversationId + ":" + (message.status || "");
    return "t:" + (message.title || "") + "|" + (message.body || "");
  }

  function rememberMessage(message) {
    var key = messageKey(message);
    if (!key) return false;
    if (seenKeys[key]) return true;
    seenKeys[key] = 1;
    seenQueue.push(key);
    if (seenQueue.length > 40) {
      var old = seenQueue.shift();
      delete seenKeys[old];
    }
    return false;
  }

  function parseCount(el) {
    if (!el) return 0;
    var n = parseInt((el.textContent || "").trim(), 10);
    return Number.isFinite(n) && n > 0 ? n : 0;
  }

  function setBellCount(count) {
    var n = Math.max(0, count | 0);
    var badges = document.querySelectorAll(".pac-notify-count, .pat-nav-badge[data-notify-badge]");
    badges.forEach(function (el) {
      el.textContent = String(n);
      if (n > 0) el.removeAttribute("hidden");
      else el.setAttribute("hidden", "");
    });

    // Sidebar "Notifications" badge on Profile (created if missing when count > 0).
    var sideLink = document.querySelector('a.pat-nav-item[href*="section=notifications"]');
    if (sideLink) {
      var sideBadge = sideLink.querySelector(".pat-nav-badge");
      if (n > 0) {
        if (!sideBadge) {
          sideBadge = document.createElement("span");
          sideBadge.className = "pat-nav-badge";
          sideBadge.setAttribute("data-notify-badge", "1");
          sideLink.appendChild(sideBadge);
        }
        sideBadge.textContent = String(n);
        sideBadge.removeAttribute("hidden");
      } else if (sideBadge) {
        sideBadge.setAttribute("hidden", "");
        sideBadge.textContent = "0";
      }
    }
  }

  function bumpBellCount(delta) {
    var bell = document.querySelector(".pac-notify-count");
    var current = parseCount(bell);
    setBellCount(current + (delta || 1));
  }

  function formatNotifTime(isoOrDate) {
    try {
      var d = isoOrDate ? new Date(isoOrDate) : new Date();
      if (Number.isNaN(d.getTime())) d = new Date();
      return d.toLocaleString(undefined, {
        month: "short",
        day: "numeric",
        hour: "numeric",
        minute: "2-digit"
      }).replace(",", " ·");
    } catch (_) {
      return "";
    }
  }

  function prependNotificationCard(message) {
    if (!/section=notifications/i.test(window.location.search || "")) return;
    var list = document.querySelector(".pi-list");
    if (!list) {
      // Empty state → create list
      var main = document.querySelector(".pat-content, .pat-main, main");
      var empty = main && main.querySelector(".pi-value");
      if (empty && /no notifications/i.test(empty.textContent || "")) {
        var panel = empty.closest(".pi-panel, .pat-panel, section") || empty.parentElement;
        if (panel) {
          panel.innerHTML = "";
          list = document.createElement("div");
          list.className = "pi-list";
          panel.appendChild(list);
        }
      }
    }
    if (!list) return;

    var title = (message && message.title) || "Nuvi update";
    var body = (message && message.body) || "";
    var slot = (message && message.slotLabel) || "";
    var doctorId = message && message.doctorId;
    var timeLabel = formatNotifTime(message && message.createdAt);

    var row = document.createElement("div");
    row.className = "pi-row pi-row-stack pi-notif-row pi-row-unread";
    var top = document.createElement("div");
    top.className = "pi-row-top";

    var bodyEl = document.createElement("div");
    bodyEl.className = "pi-row-body";
    bodyEl.innerHTML =
      '<div class="pi-value pi-notif-title"></div>' +
      '<p class="pi-hint"></p>' +
      (slot ? '<p class="pi-notif-slot"></p>' : "");
    bodyEl.querySelector(".pi-notif-title").textContent = title;
    bodyEl.querySelector(".pi-hint").textContent = body;
    if (slot) bodyEl.querySelector(".pi-notif-slot").textContent = slot;

    top.appendChild(bodyEl);
    if (doctorId) {
      var view = document.createElement("a");
      view.className = "pi-action";
      view.href = "/doctors/" + doctorId;
      view.textContent = "View";
      top.appendChild(view);
    }

    var time = document.createElement("div");
    time.className = "pi-notif-time";
    time.textContent = timeLabel;

    row.appendChild(top);
    row.appendChild(time);
    list.insertBefore(row, list.firstChild);
  }

  function handleMessage(message) {
    if (rememberMessage(message)) return;
    bumpBellCount(1);
    prependNotificationCard(message || {});
    listeners.forEach(function (fn) {
      try { fn(message); } catch (_) { /* ignore */ }
    });
  }

  async function joinGroups() {
    if (!connection || connection.state !== signalR.HubConnectionState.Connected) return;
    if (joinPatient) {
      try { await connection.invoke("JoinPatient"); } catch (_) { /* guest / claim missing */ }
    }
    if (joinedSession) {
      try { await connection.invoke("JoinSession", joinedSession); } catch (_) { /* ignore */ }
    }
  }

  async function ensureStarted(options) {
    options = options || {};
    if (options.joinPatient) joinPatient = true;

    if (typeof signalR === "undefined") {
      console.warn("[Nuvidoc] SignalR client not loaded");
      return;
    }

    if (connection && connection.state === signalR.HubConnectionState.Connected) {
      await joinGroups();
      return;
    }

    if (starting) {
      await starting;
      await joinGroups();
      return;
    }

    starting = (async function () {
      connection = new signalR.HubConnectionBuilder()
        .withUrl(HUB_PATH)
        .withAutomaticReconnect()
        .build();

      connection.on(EVENT, handleMessage);
      connection.onreconnected(async function () {
        await joinGroups();
      });

      await connection.start();
      await joinGroups();
    })();

    try {
      await starting;
    } finally {
      starting = null;
    }
  }

  async function joinSession(sessionKey) {
    if (!sessionKey) return;
    var key = String(sessionKey).trim();
    if (!key) return;
    joinedSession = key;
    await ensureStarted({});
    if (connection && connection.state === signalR.HubConnectionState.Connected) {
      try { await connection.invoke("JoinSession", key); } catch (_) { /* ignore */ }
    }
  }

  window.NuvidocPatientPush = {
    start: ensureStarted,
    joinSession: joinSession,
    setBellCount: setBellCount,
    bumpBellCount: bumpBellCount,
    onBookingUpdated: function (fn) {
      if (typeof fn === "function") listeners.push(fn);
    }
  };
})(window);
