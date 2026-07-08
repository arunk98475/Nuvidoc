let sessionKey = null;
let aiSpecialty = "";
let aiNotes = "";
let userLatitude = null;
let userLongitude = null;
let usePasswordInput = false;
let currentStage = "Greeting";
let pendingSkipToMatches = false;
let pendingCompleteMatchSearch = false;
let awaitingWildcardConcern = false;
let currentPollingQuestionKind = null;
const recommendedDoctorIds = new Set();
const pendingDoctorSelections = new Set();

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

function clearRecommendedDoctors() {
  recommendedDoctorIds.clear();
}

function markDoctorRecommended(doctorId) {
  if (doctorId != null) recommendedDoctorIds.add(Number(doctorId));
}

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
    if (!extras.selectedDoctor?.id) closeDoctorSidePanel();
    addDoctorCards(extras.doctorCards);
  }

  if (extras.selectedDoctor?.id) {
    openDoctorSidePanel(extras.selectedDoctor.id);
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
    card.dataset.doctorId = String(d.id);
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

function toVideoEmbedUrl(url) {
  if (!url) return null;
  const trimmed = url.trim();
  const yt = trimmed.match(/(?:youtube\.com\/watch\?v=|youtu\.be\/|youtube\.com\/embed\/|youtube\.com\/shorts\/)([\w-]+)/i);
  if (yt) return `https://www.youtube.com/embed/${yt[1]}`;
  const vimeo = trimmed.match(/vimeo\.com\/(?:video\/)?(\d+)/i);
  if (vimeo) return `https://player.vimeo.com/video/${vimeo[1]}`;
  const loom = trimmed.match(/loom\.com\/share\/([\w-]+)/i);
  if (loom) return `https://www.loom.com/embed/${loom[1]}`;
  if (/\.(mp4|webm|ogg)(\?|$)/i.test(trimmed)) return trimmed;
  return null;
}

function buildVideoHtml(videoUrl) {
  if (!videoUrl) return "";
  const embed = toVideoEmbedUrl(videoUrl);
  if (embed) {
    return `<div class="nuvi-profile-video">
      <iframe src="${escapeHtml(embed)}" title="Doctor introduction video" allowfullscreen loading="lazy"></iframe>
    </div>`;
  }
  if (/\.(mp4|webm|ogg)(\?|$)/i.test(videoUrl)) {
    return `<div class="nuvi-profile-video">
      <video controls preload="metadata" src="${escapeHtml(videoUrl)}"></video>
    </div>`;
  }
  return `<div class="nuvi-profile-video-link">
    <a href="${escapeHtml(videoUrl)}" target="_blank" rel="noopener noreferrer">▶ Watch introduction video</a>
  </div>`;
}

function getDoctorPublicProfileUrl(doctorId) {
  return `/doctors/${doctorId}`;
}

function openDoctorPublicProfile(doctorId) {
  if (doctorId == null) return;
  window.open(getDoctorPublicProfileUrl(doctorId), "_blank", "noopener,noreferrer");
}

function buildDoctorProfileHtml(data, { modal = false, panel = false } = {}) {
  const location = [data.city, data.state].filter((p) => p && p !== "NA").join(", ");
  const photoClass = modal ? "hp-doctor-modal-photo" : "nuvi-profile-photo";
  const avatarClass = modal ? "hp-doctor-modal-avatar" : "nuvi-profile-avatar";
  const phoneClass = modal ? "hp-doctor-modal-phone" : "nuvi-phone-link";
  const reviewClass = modal ? "hp-doctor-modal-review" : "nuvi-profile-review";
  const reviewStarsClass = modal ? "hp-doctor-modal-review-stars" : "nuvi-profile-review-stars";
  const reviewTextClass = modal ? "hp-doctor-modal-review-text" : "nuvi-profile-review-text";
  const reviewAuthorClass = modal ? "hp-doctor-modal-review-author" : "nuvi-profile-review-author";
  const sectionClass = modal ? "hp-doctor-modal-section" : "nuvi-profile-section";
  const headerClass = modal ? "hp-doctor-modal-header" : "nuvi-profile-header";
  const nameClass = modal ? "hp-doctor-modal-name" : "nuvi-profile-name";
  const specClass = modal ? "hp-doctor-modal-spec" : "nuvi-profile-spec";
  const locClass = modal ? "hp-doctor-modal-loc" : "nuvi-profile-loc";
  const ratingClass = modal ? "hp-doctor-modal-rating" : "nuvi-profile-rating";
  const nameIdAttr = modal ? ' id="doctor-modal-title"' : (panel ? ' id="doctor-side-panel-title"' : "");
  const linkable = panel || modal;

  const photoHtml = data.photoUrl
    ? `<img class="${photoClass}" src="${escapeHtml(data.photoUrl)}" alt="" />`
    : `<div class="${avatarClass}">${escapeHtml(data.avatarInitials || "DR")}</div>`;

  const phoneHtml = data.officePhoneNumber
    ? `<a class="${phoneClass}" href="tel:${data.officePhoneNumber.replace(/\D/g, "")}" onclick="event.stopPropagation()">📞 Call ${escapeHtml(data.name)} — ${escapeHtml(data.officePhoneNumber)}</a>`
    : "<p>Contact number not available</p>";

  const reviewsHtml = (data.reviews || []).length
    ? data.reviews.map((r) => {
        const metaParts = [];
        if (r.waitingTime) metaParts.push(`Waiting time: ${escapeHtml(r.waitingTime)}`);
        if (r.recommendation) metaParts.push(escapeHtml(r.recommendation));
        const metaHtml = metaParts.length
          ? `<div class="${reviewAuthorClass} nuvi-profile-review-meta">${metaParts.join(" · ")}</div>`
          : "";
        return `
        <div class="${reviewClass}">
          <div class="${reviewStarsClass}">${renderStars(r.rating)}</div>
          <div class="${reviewTextClass}">"${escapeHtml(r.reviewText)}"</div>
          ${metaHtml}
          <div class="${reviewAuthorClass}">— ${escapeHtml(r.reviewerName)}</div>
        </div>`;
      }).join("")
    : data.summaryOfReviews
      ? `<p>${escapeHtml(data.summaryOfReviews)}</p>`
      : "<p>No patient reviews yet.</p>";

  const videoHtml = buildVideoHtml(data.videoUrl);
  const openHint = linkable
    ? `<div class="nuvi-profile-open-hint">Open full profile &amp; booking times →</div>`
    : "";

  const inner = `
    <div class="${headerClass}">
      ${photoHtml}
      <div>
        <h3 class="${nameClass}"${nameIdAttr}>${escapeHtml(data.name)}</h3>
        <div class="${specClass}">${escapeHtml(data.specialty)}${data.practiceName ? ` · ${escapeHtml(data.practiceName)}` : ""}</div>
        <div class="${locClass}">${escapeHtml(location)}${data.address ? `<br>${escapeHtml(data.address)}` : ""}</div>
        ${data.googleRating > 0 ? `<div class="${ratingClass}">${renderStars(data.googleRating)} ${Number(data.googleRating).toFixed(1)} (${data.googleReviewCount || 0} Google reviews)</div>` : ""}
      </div>
    </div>
    ${videoHtml}
    <div class="${sectionClass}">
      <h4>Contact</h4>
      ${phoneHtml}
    </div>
    ${data.niche ? `<div class="${sectionClass}"><h4>Focus</h4><p>${escapeHtml(data.niche)}</p></div>` : ""}
    ${data.yearsOfPractice ? `<div class="${sectionClass}"><h4>Experience</h4><p>${data.yearsOfPractice} years in practice</p></div>` : ""}
    ${data.top3Procedures ? `<div class="${sectionClass}"><h4>Top procedures</h4><p>${escapeHtml(data.top3Procedures)}</p></div>` : ""}
    <div class="${sectionClass}">
      <h4>Reviews</h4>
      ${reviewsHtml}
    </div>
    ${openHint}`;

  if (!linkable) return inner;

  return `<div class="nuvi-profile-linkable" role="link" tabindex="0" data-doctor-id="${escapeHtml(String(data.id))}" title="Open full doctor profile">${inner}</div>`;
}

async function fetchDoctorProfile(doctorId) {
  const res = await fetch(`/api/doctors/${doctorId}`, { credentials: "same-origin" });
  const data = await res.json().catch(() => ({}));
  if (!res.ok) return null;
  return data;
}

async function openDoctorSidePanel(doctorId) {
  const wrap = document.getElementById("hero-chat-split-wrap");
  const panel = document.getElementById("hero-doctor-panel");
  const body = document.getElementById("hero-doctor-panel-body");
  if (!wrap || !panel || !body) {
    await addDoctorProfileInChat(doctorId);
    return;
  }

  highlightDoctorCard(doctorId);
  wrap.classList.add("is-split");
  document.getElementById("hero-section")?.classList.add("has-doctor-panel");
  panel.hidden = false;
  panel.setAttribute("aria-hidden", "false");
  body.innerHTML = '<div class="hero-doctor-panel-loading">Loading doctor profile…</div>';

  try {
    const data = await fetchDoctorProfile(doctorId);
    if (!data) {
      body.innerHTML = '<div class="hero-doctor-panel-loading">Unable to load this doctor profile.</div>';
      return;
    }
    body.innerHTML = buildDoctorProfileHtml(data, { panel: true });
    const linkable = body.querySelector(".nuvi-profile-linkable");
    if (linkable) {
      const openProfile = () => openDoctorPublicProfile(data.id);
      linkable.onclick = (e) => {
        if (e.target.closest("a, button, iframe, video")) return;
        openProfile();
      };
      linkable.onkeydown = (e) => {
        if (e.key === "Enter" || e.key === " ") {
          e.preventDefault();
          openProfile();
        }
      };
    }
  } catch {
    body.innerHTML = '<div class="hero-doctor-panel-loading">Unable to load this doctor profile.</div>';
  }

  panel.scrollIntoView({ behavior: "smooth", block: "nearest" });
}

function closeDoctorSidePanel() {
  const wrap = document.getElementById("hero-chat-split-wrap");
  const panel = document.getElementById("hero-doctor-panel");
  wrap?.classList.remove("is-split");
  document.getElementById("hero-section")?.classList.remove("has-doctor-panel");
  if (panel) {
    panel.hidden = true;
    panel.setAttribute("aria-hidden", "true");
  }
  highlightDoctorCard(null);
}

function highlightDoctorCard(doctorId) {
  document.querySelectorAll(".nuvi-doctor-card").forEach((card) => {
    const id = card.dataset.doctorId;
    card.classList.toggle("selected", doctorId != null && id === String(doctorId));
  });
}

async function addDoctorProfileInChat(doctorId) {
  const msgs = document.getElementById("chat-messages");
  const wrap = document.createElement("div");
  wrap.className = "msg ai nuvi-profile-wrap";
  wrap.innerHTML = `<div class="msg-avatar">${NUVI_AVATAR}</div>
    <div class="msg-bubble nuvi-profile-bubble">
      <div class="nuvi-profile-loading">Loading doctor profile…</div>
    </div>`;
  msgs.appendChild(wrap);
  msgs.scrollTop = msgs.scrollHeight;

  const bubble = wrap.querySelector(".nuvi-profile-bubble");
  try {
    const data = await fetchDoctorProfile(doctorId);
    if (!data) {
      bubble.innerHTML = '<div class="nuvi-profile-loading">Unable to load this doctor profile.</div>';
      return;
    }
    bubble.innerHTML = buildDoctorProfileHtml(data, { modal: false });
  } catch {
    bubble.innerHTML = '<div class="nuvi-profile-loading">Unable to load this doctor profile.</div>';
  }
  msgs.scrollTop = msgs.scrollHeight;
}

function setChips(options) {
  const chipsEl = document.getElementById("quick-chips");
  removeLanguageSelector();
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
    if (currentPollingQuestionKind === "wildcard" && /^no$/i.test(opt)) {
      btn.dataset.completeMatchSearch = "true";
    }
    btn.onclick = () => sendChip(btn);
    chipsEl.appendChild(btn);
  });
}

function removeLanguageSelector() {
  document.getElementById("language-select-wrap")?.remove();
}

function addLanguageSelector(languages) {
  const chipsEl = document.getElementById("quick-chips");
  removeLanguageSelector();
  chipsEl.innerHTML = "";
  chipsEl.style.display = "flex";

  const wrap = document.createElement("div");
  wrap.className = "nuvi-language-select-wrap";
  wrap.id = "language-select-wrap";

  const select = document.createElement("select");
  select.className = "nuvi-language-select";
  select.innerHTML =
    '<option value="">Select a language...</option>' +
    languages.map((lang) => `<option value="${escapeHtml(lang)}">${escapeHtml(lang)}</option>`).join("");

  const btn = document.createElement("button");
  btn.type = "button";
  btn.className = "chip";
  btn.textContent = "Confirm";
  btn.onclick = () => {
    if (!select.value) return;
    document.getElementById("chat-input").value = select.value;
    sendMessage();
  };

  wrap.appendChild(select);
  wrap.appendChild(btn);
  chipsEl.appendChild(wrap);
}

function updateChatPlaceholder(text) {
  const input = document.getElementById("chat-input");
  if (!input || usePasswordInput) return;
  input.placeholder = text || `Tell ${branding.chatBotName} what's going on...`;
}

function applyInputLock(optionsOnly) {
  const input = document.getElementById("chat-input");
  const sendBtn = document.getElementById("send-btn");
  if (!input) return;

  if (optionsOnly) {
    input.value = "";
    input.disabled = true;
    input.placeholder = "Tap an option above to continue";
    input.classList.add("input-locked");
    if (sendBtn) sendBtn.disabled = true;
  } else {
    input.disabled = false;
    input.classList.remove("input-locked");
    if (sendBtn) sendBtn.disabled = false;
  }
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

async function fetchChatMessage(body) {
  const res = await fetch("/api/chat/message", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    credentials: "same-origin",
    body: JSON.stringify(body)
  });
  const data = await res.json().catch(() => ({}));
  return { ok: res.ok, status: res.status, data };
}

function applyChatResponseState(data) {
  sessionKey = data.sessionKey;
  if (data.specialty) aiSpecialty = data.specialty;
  if (data.notes) aiNotes = data.notes;
  if (data.stage) currentStage = data.stage;
  awaitingWildcardConcern = !!data.awaitingWildcardConcern;
  currentPollingQuestionKind = data.pollingQuestionKind || null;

  updateInputMode(data.usePasswordInput);
  updateChatPlaceholder(data.inputPlaceholder);

  if (data.languageOptions?.length) {
    addLanguageSelector(data.languageOptions);
  } else {
    setChips(data.options);
  }

  applyInputLock(!!data.optionsOnly);

  if (data.signedIn) {
    updateNavForSignedInPatient();
  }

  const input = document.getElementById("chat-input");
  if (data.flowComplete && input) {
    setChips([]);
    input.placeholder = "Conversation complete — refresh to start over";
    input.disabled = true;
    document.getElementById("send-btn").disabled = true;
  }
}

async function sendMessage(action = null, selectedDoctorId = null) {
  const input = document.getElementById("chat-input");
  const text = input.value.trim();
  if (!text && !action && !selectedDoctorId) return;

  const doctorIdNum = selectedDoctorId != null ? Number(selectedDoctorId) : null;
  const isRepeatDoctorSelection = doctorIdNum != null && recommendedDoctorIds.has(doctorIdNum);

  if (isRepeatDoctorSelection) {
    openDoctorSidePanel(doctorIdNum);
    return;
  }

  const wasPasswordInput = usePasswordInput;
  const completeMatchSearch =
    pendingCompleteMatchSearch ||
    (awaitingWildcardConcern && !!text);
  pendingCompleteMatchSearch = false;

  const skipToMatches =
    pendingSkipToMatches ||
    (currentStage === "DeepDivePermission" && isSkipToMatchesMessage(text));
  pendingSkipToMatches = false;

  const pendingMatchSearch = skipToMatches || completeMatchSearch;

  input.value = "";
  if (input.tagName === "TEXTAREA") autoResize(input);
  document.getElementById("send-btn").disabled = true;

  if (text) addMessage(wasPasswordInput ? "••••••••" : text, "user");

  const matchSearchStartedAt = pendingMatchSearch ? Date.now() : 0;
  if (pendingMatchSearch) {
    addMessage(MATCH_SEARCH_LOADING_MESSAGE, "ai", { loading: true });
  } else if (!isRepeatDoctorSelection) {
    showTyping();
  }

  try {
    const { ok, status, data } = await fetchChatMessage({
      sessionKey,
      message: text || (selectedDoctorId ? "" : (action ? action : "continue")),
      action,
      selectedDoctorId
    });

    if (!ok) {
      removeTyping();
      const errText = data.title || data.detail || data.message || data.error || `Server error (${status})`;
      addMessage(`Sorry — ${errText}. Please try again.`, "ai");
      document.getElementById("send-btn").disabled = false;
      return;
    }

    removeTyping();

    if (data.awaitingMatchSearch) {
      if (!pendingMatchSearch) {
        addMessage(data.text || MATCH_SEARCH_LOADING_MESSAGE, "ai", { loading: true });
      }

      applyChatResponseState(data);

      const matchSearchStartedAt = Date.now();
      const searchResult = await fetchChatMessage({
        sessionKey,
        action: "match_search",
        message: ""
      });

      if (!searchResult.ok) {
        const errText = searchResult.data.title || searchResult.data.detail
          || searchResult.data.message || searchResult.data.error
          || `Server error (${searchResult.status})`;
        addMessage(`Sorry — ${errText}. Please try again.`, "ai");
        document.getElementById("send-btn").disabled = false;
        return;
      }

      const searchData = searchResult.data;
      const elapsed = Date.now() - matchSearchStartedAt;
      const minWait = 1200;
      if (elapsed < minWait) {
        await delay(minWait - elapsed);
      }

      addMessage(searchData.text || "Here are your matches.", "ai", {
        doctorCards: searchData.doctorCards,
        selectedDoctor: searchData.selectedDoctor
      });
      if (searchData.doctorCards?.length) clearRecommendedDoctors();
      applyChatResponseState(searchData);
      document.getElementById("send-btn").disabled = false;
      return;
    }

    if (data.followUpText && (data.showLoading || pendingMatchSearch)) {
      if (!pendingMatchSearch) {
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
      if (selectedDoctorId) markDoctorRecommended(selectedDoctorId);
      else if (data.selectedDoctor?.id) markDoctorRecommended(data.selectedDoctor.id);
    } else {
      if (data.showLoading) {
        await delay(2500);
      }

      const isDuplicateLoading =
        pendingMatchSearch &&
        (data.text || "") === MATCH_SEARCH_LOADING_MESSAGE;
      if (!isDuplicateLoading) {
        const hasAiText = !!(data.text && data.text.trim());
        if (hasAiText || data.doctorCards?.length) {
          addMessage(data.text || "I'm here to help. Could you tell me more?", "ai", {
            loading: data.showLoading,
            doctorCards: data.doctorCards,
            selectedDoctor: data.selectedDoctor
          });
        } else if (data.selectedDoctor?.id) {
          openDoctorSidePanel(data.selectedDoctor.id);
        }
      }

      if (selectedDoctorId) markDoctorRecommended(selectedDoctorId);
      else if (data.selectedDoctor?.id) markDoctorRecommended(data.selectedDoctor.id);
    }

    applyChatResponseState(data);
  } catch {
    removeTyping();
    addMessage("I'm having trouble connecting right now. Please try again.", "ai");
  } finally {
    if (doctorIdNum != null) pendingDoctorSelections.delete(doctorIdNum);
  }

  const chatInput = document.getElementById("chat-input");
  if (!chatInput || !chatInput.classList.contains("input-locked")) {
    document.getElementById("send-btn").disabled = false;
  }
}

function selectDoctor(doctorId) {
  const doctorIdNum = Number(doctorId);
  openDoctorSidePanel(doctorIdNum);
  if (recommendedDoctorIds.has(doctorIdNum) || pendingDoctorSelections.has(doctorIdNum)) return;
  pendingDoctorSelections.add(doctorIdNum);
  sendMessage(null, doctorIdNum);
}

function sendChip(btn) {
  pendingSkipToMatches = btn.dataset.skipToMatches === "true";
  pendingCompleteMatchSearch = btn.dataset.completeMatchSearch === "true";
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

function renderStars(rating) {
  const count = Math.max(0, Math.min(5, Math.round(Number(rating) || 0)));
  return "★".repeat(count) + "☆".repeat(5 - count);
}

async function openDoctorProfileModal(doctorId) {
  const modal = document.getElementById("doctor-profile-modal");
  const body = document.getElementById("doctor-profile-modal-body");
  if (!modal || !body) return;

  modal.hidden = false;
  modal.setAttribute("aria-hidden", "false");
  document.body.style.overflow = "hidden";
  body.innerHTML = '<div class="hp-doctor-modal-loading">Loading profile…</div>';

  try {
    const data = await fetchDoctorProfile(doctorId);
    if (!data) {
      body.innerHTML = '<div class="hp-doctor-modal-loading">Unable to load this doctor profile.</div>';
      return;
    }
    body.innerHTML = buildDoctorProfileHtml(data, { modal: true });
    const linkable = body.querySelector(".nuvi-profile-linkable");
    if (linkable) {
      const openProfile = () => openDoctorPublicProfile(data.id);
      linkable.onclick = (e) => {
        if (e.target.closest("a, button, iframe, video")) return;
        openProfile();
      };
      linkable.onkeydown = (e) => {
        if (e.key === "Enter" || e.key === " ") {
          e.preventDefault();
          openProfile();
        }
      };
    }
  } catch {
    body.innerHTML = '<div class="hp-doctor-modal-loading">Unable to load this doctor profile.</div>';
  }
}

function closeDoctorProfileModal() {
  const modal = document.getElementById("doctor-profile-modal");
  if (!modal) return;
  modal.hidden = true;
  modal.setAttribute("aria-hidden", "true");
  document.body.style.overflow = "";
}

document.addEventListener("keydown", (e) => {
  if (e.key === "Escape") closeDoctorProfileModal();
});
