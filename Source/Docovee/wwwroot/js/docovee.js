let sessionKey = null;
let aiSpecialty = "";
let aiNotes = "";
let userLatitude = null;
let userLongitude = null;
let usePasswordInput = false;
let currentStage = "Greeting";

const branding = window.nuvidocBranding || { siteName: "NuviDoc", chatBotName: "Nuvi", chatBotInitial: "N" };
const NUVI_AVATAR = branding.chatBotInitial;

document.addEventListener("DOMContentLoaded", () => {
  requestLocation();
});

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
  const input = document.getElementById("chat-input");
  input.type = passwordMode ? "password" : "text";
  input.placeholder = passwordMode ? "Enter your password..." : "Type your message...";
}

async function sendMessage(action = null, selectedDoctorId = null) {
  const input = document.getElementById("chat-input");
  const text = input.value.trim();
  if (!text && !action && !selectedDoctorId) return;

  input.value = "";
  document.getElementById("send-btn").disabled = true;

  if (text) addMessage(text, "user");
  showTyping();

  try {
    const res = await fetch("/api/chat/message", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
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

    if (data.showLoading) {
      await delay(1500);
    }

    addMessage(data.text || "I'm here to help. Could you tell me more?", "ai", {
      loading: data.showLoading,
      doctorCards: data.doctorCards,
      selectedDoctor: data.selectedDoctor
    });

    sessionKey = data.sessionKey;
    if (data.specialty) aiSpecialty = data.specialty;
    if (data.notes) aiNotes = data.notes;
    if (data.stage) currentStage = data.stage;

    updateInputMode(data.usePasswordInput);
    setChips(data.options);

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
  document.getElementById("chat-input").value = btn.textContent;
  sendMessage();
}

function handleKey(e) {
  if (e.key === "Enter") {
    e.preventDefault();
    sendMessage();
  }
}

function delay(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

// Legacy screen flow kept for profile/search fallback (unused in Nuvi chat flow)
function goToScreen(n) {
  if (n >= 2) {
    document.getElementById("hero-section").style.display = "none";
    document.getElementById("main-flow").style.display = "block";
  }
  document.querySelectorAll(".screen").forEach((s) => s.classList.remove("active"));
  const target = document.getElementById(`screen-${n}`);
  if (target) target.classList.add("active");
  if (n === 3) searchDoctors();
  window.scrollTo({ top: 0, behavior: "smooth" });
}

async function searchDoctors() {
  const location = document.getElementById("pref-location")?.value.trim() || "Renton, WA";
  const insurancePlan = document.getElementById("insurance-input")?.value.trim() || null;
  const genderPref = document.querySelector('input[name="gender"]:checked')?.value || "none";
  const comm = document.querySelector('input[name="comm"]:checked')?.value;
  const avail = document.querySelector('input[name="avail"]:checked')?.value;

  document.getElementById("match-headline").textContent = `Your top ${aiSpecialty || "doctor"} matches`;
  document.getElementById("match-subhead").textContent = aiNotes
    ? `Matched for you: ${aiNotes}`
    : "Ranked by Google Reviews and fit for your needs.";

  const list = document.getElementById("provider-list");
  list.innerHTML = '<div class="card"><p>Searching for doctors...</p></div>';

  try {
    const res = await fetch("/api/doctors/search", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        sessionKey,
        location,
        latitude: userLatitude,
        longitude: userLongitude,
        insurancePlan,
        genderPreference: genderPref,
        communicationStyle: comm,
        availabilityPreference: avail
      })
    });

    const doctors = await res.json();
    list.innerHTML = "";

    if (!doctors.length) {
      list.innerHTML = '<div class="card"><p>No doctors found for your criteria. Try adjusting your location or preferences.</p></div>';
      return;
    }

    doctors.forEach((p) => {
      const card = document.createElement("div");
      card.className = `provider-card${p.recommended ? " recommended" : ""}`;
      card.innerHTML = `
        ${p.recommended ? '<div class="rec-badge">Best Match</div>' : ""}
        <div class="provider-top">
          <div class="provider-avatar">${escapeHtml(p.avatarInitials)}</div>
          <div class="provider-info">
            <div class="provider-name">${escapeHtml(p.name)}</div>
            <div class="provider-spec">${escapeHtml(p.specialty)}</div>
          </div>
          <div class="match-score">
            <div class="match-num">${p.matchScore}</div>
            <div class="match-label">Fit Score</div>
          </div>
        </div>`;
      list.appendChild(card);
    });
  } catch {
    list.innerHTML = '<div class="card"><p>Unable to load doctors. Please try again.</p></div>';
  }
}

function showRegisterModal() {
  document.getElementById("register-modal")?.classList.add("active");
}

function closeRegisterModal() {
  document.getElementById("register-modal")?.classList.remove("active");
}

async function submitRegistration() {
  const errorEl = document.getElementById("register-error");
  errorEl.style.display = "none";

  const payload = {
    sessionKey,
    fullName: document.getElementById("reg-name").value.trim(),
    dateOfBirth: document.getElementById("reg-dob").value,
    phone: document.getElementById("reg-phone").value.trim(),
    username: document.getElementById("reg-username").value.trim(),
    password: document.getElementById("reg-password").value
  };

  if (!payload.fullName || !payload.dateOfBirth || !payload.phone || !payload.username || !payload.password) {
    errorEl.textContent = "Please fill in all fields.";
    errorEl.style.display = "block";
    return;
  }

  try {
    const res = await fetch("/api/patients/register", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(payload)
    });
    const data = await res.json();
    if (!res.ok) {
      errorEl.textContent = data.message || "Registration failed.";
      errorEl.style.display = "block";
      return;
    }
    closeRegisterModal();
    alert("Account created! Your search has been saved.");
  } catch {
    errorEl.textContent = "Registration failed. Please try again.";
    errorEl.style.display = "block";
  }
}
