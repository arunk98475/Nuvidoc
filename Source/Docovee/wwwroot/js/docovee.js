let sessionKey = null;
let aiSpecialty = "";
let aiNotes = "";
let userLatitude = null;
let userLongitude = null;
let usePasswordInput = false;
let currentStage = "Greeting";
let pendingSkipToMatches = false;

const branding = window.nuvidocBranding || { siteName: "NuviDoc", chatBotName: "Nuvi" };
const NUVI_AVATAR = branding.chatBotName;
const MATCH_SEARCH_LOADING_MESSAGE =
  branding.matchSearchLoadingMessage ||
  "Please wait for a while — I'm searching for the best matches for you.";

document.addEventListener("DOMContentLoaded", () => {
  requestLocation();
  const chatInput = document.getElementById("chat-input");
  if (chatInput?.tagName === "TEXTAREA") autoResize(chatInput);
});

function scrollToChat() {
  window.scrollTo({ top: 0, behavior: "smooth" });
  const input = document.getElementById("chat-input");
  if (input) setTimeout(() => input.focus(), 400);
}

function fillChatInput(text) {
  const input = document.getElementById("chat-input");
  if (!input) return;
  input.value = text;
  if (input.tagName === "TEXTAREA") autoResize(input);
  scrollToChat();
}

function autoResize(el) {
  if (!el || el.tagName !== "TEXTAREA") return;
  el.style.height = "auto";
  el.style.height = Math.min(el.scrollHeight, 120) + "px";
}

function ensureChatInputElement(passwordMode) {
  const area = document.getElementById("chat-input-area");
  let input = document.getElementById("chat-input");
  if (!area || !input) return input;

  const value = input.value;
  const defaultPlaceholder = `Tell ${branding.chatBotName} what's going on...`;

  if (passwordMode && input.tagName === "TEXTAREA") {
    const newInput = document.createElement("input");
    newInput.type = "password";
    newInput.id = "chat-input";
    newInput.className = "chat-input";
    newInput.name = "new-password";
    newInput.autocomplete = "new-password";
    newInput.placeholder = "Enter your password...";
    newInput.value = value;
    newInput.onkeydown = handleKey;
    input.replaceWith(newInput);
    return newInput;
  }

  if (!passwordMode && input.tagName === "INPUT") {
    const newTa = document.createElement("textarea");
    newTa.id = "chat-input";
    newTa.className = "chat-input";
    newTa.rows = 1;
    newTa.name = "chat-message";
    newTa.autocomplete = "off";
    newTa.placeholder = defaultPlaceholder;
    newTa.value = value;
    newTa.onkeydown = handleKey;
    newTa.oninput = function () { autoResize(this); };
    input.replaceWith(newTa);
    autoResize(newTa);
    return newTa;
  }

  return input;
}

function updateNavForSignedInPatient() {
  const navRight = document.getElementById("nav-right");
  if (!navRight || navRight.dataset.authenticated === "true") return;

  navRight.querySelector(".nav-for-doctors")?.remove();
  navRight.querySelector('a[href="/Account/Login"]')?.remove();

  const cta = navRight.querySelector(".nav-cta");

  const profile = document.createElement("a");
  profile.href = "/Account/Profile";
  profile.className = "nav-link";
  profile.textContent = "My Profile";

  const logout = document.createElement("a");
  logout.href = "/Account/Logout";
  logout.className = "nav-link";
  logout.textContent = "Logout";

  navRight.insertBefore(profile, cta);
  navRight.insertBefore(logout, cta);
  navRight.dataset.authenticated = "true";
}

async function requestLocation() {
  if (!navigator.geolocation) return;
  navigator.geolocation.getCurrentPosition(
    (pos) => {
      userLatitude = pos.coords.latitude;
      userLongitude = pos.coords.longitude;
    },
    () => {},
    { enableHighAccuracy: false, timeout: 8000 }
  );
}

function addMessage(text, role, extras = {}) {
  const msgs = document.getElementById("chat-messages");
  const div = document.createElement("div");
  div.className = `msg ${role}`;
  let bubbleContent = escapeHtml(text).replace(/\n/g, "<br>");

  if (extras.loading) {
    bubbleContent = '<span class="nuvi-loading"><span></span><span></span><span></span></span> ' + bubbleContent;
  }

  div.innerHTML = `
    <div class="msg-avatar">${role === "ai" ? NUVI_AVATAR : "Y"}</div>
    <div class="msg-bubble">${bubbleContent}</div>`;
  msgs.appendChild(div);

  if (extras.doctorCards?.length) {
    addDoctorCards(extras.doctorCards);
  }

  if (extras.selectedDoctor?.officePhoneNumber) {
    addPhoneLink(extras.selectedDoctor);
  }

  msgs.scrollTop = msgs.scrollHeight;
}

function addDoctorCards(doctors) {
  const msgs = document.getElementById("chat-messages");
  const wrap = document.createElement("div");
  wrap.className = "msg ai nuvi-doctor-cards-wrap";
  wrap.innerHTML = `<div class="msg-avatar">${NUVI_AVATAR}</div><div class="nuvi-doctor-cards"></div>`;
  const container = wrap.querySelector(".nuvi-doctor-cards");

  doctors.forEach((d, i) => {
    const card = document.createElement("button");
    card.type = "button";
    card.className = "nuvi-doctor-card" + (d.recommended ? " recommended" : "");
    card.innerHTML = `
      ${d.recommended ? '<div class="nuvi-rec-badge">Best Match</div>' : ""}
      <div class="nuvi-doctor-card-top">
        <div class="nuvi-doctor-avatar">${escapeHtml(d.avatarInitials)}</div>
        <div>
          <div class="nuvi-doctor-name">${escapeHtml(d.name)}</div>
          <div class="nuvi-doctor-spec">${escapeHtml(d.specialty)}</div>
          <div class="nuvi-doctor-loc">${escapeHtml(d.location)}</div>
        </div>
        <div class="nuvi-match-score">
          <div class="nuvi-match-num">${d.matchScore}</div>
          <div class="nuvi-match-label">Fit</div>
        </div>
      </div>
      ${d.matchReason ? `<div class="nuvi-doctor-reason">${escapeHtml(d.matchReason)}</div>` : ""}
      <div class="nuvi-doctor-tag">${escapeHtml(d.tag || "")}</div>`;
    card.onclick = () => selectDoctor(d.id);
    container.appendChild(card);
  });

  msgs.appendChild(wrap);
  msgs.scrollTop = msgs.scrollHeight;
}

function addPhoneLink(doctor) {
  const msgs = document.getElementById("chat-messages");
  const wrap = document.createElement("div");
  wrap.className = "msg ai";
  const phone = doctor.officePhoneNumber.replace(/\D/g, "");
  const display = doctor.officePhoneNumber;
  wrap.innerHTML = `
    <div class="msg-avatar">${NUVI_AVATAR}</div>
    <div class="msg-bubble">
      <a href="tel:${phone}" class="nuvi-phone-link">📞 Call ${escapeHtml(doctor.name)} — ${escapeHtml(display)}</a>
      ${doctor.officeHours ? `<div class="nuvi-office-hours">Hours: ${escapeHtml(doctor.officeHours)}</div>` : ""}
    </div>`;
  msgs.appendChild(wrap);
  msgs.scrollTop = msgs.scrollHeight;
}

function setChips(options) {
  const chipsEl = document.getElementById("quick-chips");
  chipsEl.innerHTML = "";
  if (!options?.length) {
    chipsEl.style.display = "none";
    return;
  }
  chipsEl.style.display = "flex";
  options.forEach((opt) => {
    const btn = document.createElement("button");
    btn.className = "chip";
    btn.textContent = opt;
    if (/no thanks|show my match/i.test(opt)) {
      btn.dataset.skipToMatches = "true";
    }
    btn.onclick = () => sendChip(btn);
    chipsEl.appendChild(btn);
  });
}

function escapeHtml(text) {
  const div = document.createElement("div");
  div.textContent = text ?? "";
  return div.innerHTML;
}

function showTyping() {
  const msgs = document.getElementById("chat-messages");
  const div = document.createElement("div");
  div.className = "msg ai";
  div.id = "typing-msg";
  div.innerHTML = `<div class="msg-avatar">${NUVI_AVATAR}</div><div class="msg-bubble"><span class="nuvi-loading"><span></span><span></span><span></span></span></div>`;
  msgs.appendChild(div);
  msgs.scrollTop = msgs.scrollHeight;
}

function removeTyping() {
  const t = document.getElementById("typing-msg");
  if (t) t.remove();
}

function updateInputMode(passwordMode) {
  usePasswordInput = passwordMode;
  const input = ensureChatInputElement(passwordMode);
  if (!input) return;

  if (input.tagName === "INPUT") {
    input.type = passwordMode ? "password" : "text";
    input.name = passwordMode ? "new-password" : "chat-message";
    input.autocomplete = passwordMode ? "new-password" : "off";
  } else {
    input.name = "chat-message";
    input.autocomplete = "off";
  }

  input.placeholder = passwordMode
    ? "Enter your password..."
    : `Tell ${branding.chatBotName} what's going on...`;
}

function isSkipToMatchesMessage(text) {
  const lower = (text || "").toLowerCase();
  return lower.includes("no thanks") || lower.includes("show my match");
}

async function sendMessage(action = null, selectedDoctorId = null) {
  const input = document.getElementById("chat-input");
  const text = input.value.trim();
  if (!text && !action && !selectedDoctorId) return;

  const wasPasswordInput = usePasswordInput;
  const skipToMatches =
    pendingSkipToMatches ||
    (currentStage === "DeepDivePermission" && isSkipToMatchesMessage(text));
  pendingSkipToMatches = false;

  input.value = "";
  if (input.tagName === "TEXTAREA") autoResize(input);
  document.getElementById("send-btn").disabled = true;

  if (text) addMessage(wasPasswordInput ? "••••••••" : text, "user");

  const matchSearchStartedAt = skipToMatches ? Date.now() : 0;
  if (skipToMatches) {
    addMessage(MATCH_SEARCH_LOADING_MESSAGE, "ai", { loading: true });
  } else {
    showTyping();
  }

  try {
    const res = await fetch("/api/chat/message", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      credentials: "same-origin",
      body: JSON.stringify({
        sessionKey,
        message: text || (action ? action : "continue"),
        action,
        selectedDoctorId
      })
    });

    const data = await res.json().catch(() => ({}));

    if (!res.ok) {
      removeTyping();
      const errText = data.title || data.detail || data.message || data.error || `Server error (${res.status})`;
      addMessage(`Sorry — ${errText}. Please try again.`, "ai");
      document.getElementById("send-btn").disabled = false;
      return;
    }

    removeTyping();

    if (data.showLoading && data.followUpText) {
      if (!skipToMatches) {
        addMessage(data.text || MATCH_SEARCH_LOADING_MESSAGE, "ai", { loading: true });
        await delay(2500);
      } else {
        const elapsed = Date.now() - matchSearchStartedAt;
        const minWait = 1200;
        if (elapsed < minWait) {
          await delay(minWait - elapsed);
        }
      }

      addMessage(data.followUpText, "ai", {
        doctorCards: data.doctorCards,
        selectedDoctor: data.selectedDoctor
      });
    } else {
      if (data.showLoading) {
        await delay(2500);
      }

      addMessage(data.text || "I'm here to help. Could you tell me more?", "ai", {
        loading: data.showLoading,
        doctorCards: data.doctorCards,
        selectedDoctor: data.selectedDoctor
      });
    }

    sessionKey = data.sessionKey;
    if (data.specialty) aiSpecialty = data.specialty;
    if (data.notes) aiNotes = data.notes;
    if (data.stage) currentStage = data.stage;

    updateInputMode(data.usePasswordInput);
    setChips(data.options);

    if (data.signedIn) {
      updateNavForSignedInPatient();
    }

    if (data.flowComplete) {
      setChips([]);
      input.placeholder = "Conversation complete — refresh to start over";
      input.disabled = true;
      document.getElementById("send-btn").disabled = true;
    }
  } catch {
    removeTyping();
    addMessage("I'm having trouble connecting right now. Please try again.", "ai");
  }

  document.getElementById("send-btn").disabled = false;
}

function selectDoctor(doctorId) {
  sendMessage(null, doctorId);
}

function sendChip(btn) {
  pendingSkipToMatches = btn.dataset.skipToMatches === "true";
  document.getElementById("chat-input").value = btn.textContent;
  sendMessage();
}

function handleKey(e) {
  if (e.key === "Enter" && !e.shiftKey) {
    e.preventDefault();
    sendMessage();
  }
}

function delay(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}
