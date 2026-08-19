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
/** @type {Map<number, string>} AI concierge notes keyed by doctor id (shown atop the detail panel). */
const doctorAiComments = new Map();
/** Doctor id currently shown in the side panel (if any). */
let panelDoctorId = null;
/** True while waiting for Nuvi's recommendation text for the open panel doctor. */
let panelAiLoading = false;

const branding = window.nuvidocBranding || { siteName: "NuviDoc", chatBotName: "Nuvi" };
const NUVI_AVATAR = branding.chatBotName;
const MATCH_SEARCH_LOADING_MESSAGE =
  branding.matchSearchLoadingMessage ||
  "Please wait for a while — I'm searching for the best matches for you.";
const seenCallChatKeys = new Set();
let callResultChatPushBound = false;

document.addEventListener("DOMContentLoaded", () => {
  requestLocation();
  initCallResultChatPush();
  const chatInput = document.getElementById("chat-input");
  if (chatInput?.tagName === "TEXTAREA") autoResize(chatInput);

  const params = new URLSearchParams(window.location.search);
  const isSignup = params.get("signup") === "patient" || params.get("signup") === "1";
  if (isSignup) {
    startPatientSignupViaChat();
    const url = new URL(window.location.href);
    url.searchParams.delete("signup");
    window.history.replaceState({}, "", url.pathname + url.search + url.hash);
  } else {
    const focusChat = params.get("chat") === "1" || params.get("chat") === "true";
    if (focusChat) {
      scrollToChat();
      const url = new URL(window.location.href);
      url.searchParams.delete("chat");
      window.history.replaceState({}, "", url.pathname + url.search + url.hash);
    }
    playWelcomeIntro({ focusInput: focusChat });
  }
});

/** Brief typing indicator, then first welcome bubble + quick-reply chips. */
function playWelcomeIntro(options = {}) {
  const msgs = document.getElementById("chat-messages");
  const welcome = branding.welcomeMessage;
  if (!msgs || !welcome) return;

  const chipsEl = document.getElementById("quick-chips");
  const input = document.getElementById("chat-input");
  const sendBtn = document.getElementById("send-btn");
  if (chipsEl) {
    chipsEl.style.display = "none";
    chipsEl.setAttribute("aria-hidden", "true");
  }
  if (input) input.disabled = true;
  if (sendBtn) sendBtn.disabled = true;

  showTyping();

  const typingMs = 1400;
  setTimeout(() => {
    removeTyping();
    addMessage(welcome, "ai");
    if (chipsEl) {
      chipsEl.style.display = "flex";
      chipsEl.removeAttribute("aria-hidden");
      chipsEl.classList.add("chips-reveal");
    }
    if (input) input.disabled = false;
    if (sendBtn) sendBtn.disabled = false;
    if (branding.welcomeChips?.length) {
      setChips(branding.welcomeChips);
      applyInputLock(true);
    }
    if (options.focusInput) input?.focus();
  }, typingMs);
}

function startPatientSignupViaChat() {
  scrollToChat();
  if (!document.getElementById("chat-input")) return;

  const input = document.getElementById("chat-input");
  if (input) input.value = "";
  document.getElementById("send-btn").disabled = true;
  showTyping();

  fetchChatMessage({
    sessionKey,
    message: "",
    action: "signup"
  }).then(({ ok, status, data }) => {
    removeTyping();
    document.getElementById("send-btn").disabled = false;
    if (!ok) {
      const errText = data.title || data.detail || data.message || data.error || `Server error (${status})`;
      addMessage(errText, "ai");
      return;
    }
    if (data.text) addMessage(data.text, "ai");
    applyChatResponseState(data);
  }).catch(() => {
    removeTyping();
    document.getElementById("send-btn").disabled = false;
    addMessage("Something went wrong starting sign up. Please try again.", "ai");
  });
}

window.startPatientSignupViaChat = startPatientSignupViaChat;

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

  navRight.querySelector("#nav-auth")?.remove();
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

  if (cta) {
    navRight.insertBefore(profile, cta);
    navRight.insertBefore(logout, cta);
  } else {
    navRight.appendChild(profile);
    navRight.appendChild(logout);
  }
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
  const trimmed = (text ?? "").trim();
  // Doctor explore copy belongs on the detail panel (top), not as a chat bubble.
  const moveAiCommentToPanel =
    role === "ai" &&
    extras.selectedDoctor?.id &&
    !extras.doctorCards?.length &&
    !extras.loading &&
    !!trimmed;

  if (moveAiCommentToPanel) {
    const doctorId = extras.selectedDoctor.id;
    doctorAiComments.set(Number(doctorId), trimmed);
    setDoctorPanelAiState(doctorId, { aiComment: trimmed, aiLoading: false });
    return;
  }

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

  scrollChatToBottom();
}

function scrollChatToBottom() {
  const msgs = document.getElementById("chat-messages");
  if (!msgs) return;
  const stick = () => {
    msgs.scrollTop = msgs.scrollHeight;
  };
  stick();
  requestAnimationFrame(() => {
    stick();
    requestAnimationFrame(stick);
  });
}

function callChatMessageKey(message) {
  if (!message) return "";
  const text = (message.chatMessage || "").trim();
  if (!text) return "";
  if (message.conversationId) return `chat:${message.conversationId}:${text}`;
  return `chat:${text}`;
}

function initCallResultChatPush() {
  if (callResultChatPushBound || !document.getElementById("chat-messages")) return;
  if (!window.NuvidocPatientPush?.onBookingUpdated) return;
  callResultChatPushBound = true;
  window.NuvidocPatientPush.onBookingUpdated((message) => {
    if (!document.getElementById("chat-messages")) return;
    const text = (message.chatMessage || "").trim();
    if (!text) return;
    const msgSession = message?.sessionKey ? String(message.sessionKey).toLowerCase() : "";
    const openSession = sessionKey ? String(sessionKey).toLowerCase() : "";
    const sameSession = !!msgSession && !!openSession && msgSession === openSession;
    const signedIn = document.getElementById("nav-right")?.dataset?.authenticated === "true";
    // Cancel/reschedule often belongs to an older booking session; still show it in the open Nuvi chat.
    if (!sameSession && !(signedIn && text)) return;
    const key = callChatMessageKey(message);
    if (key && seenCallChatKeys.has(key)) return;
    if (key) seenCallChatKeys.add(key);
    addMessage(text, "ai");
    if (Array.isArray(message.chatOptions) && message.chatOptions.length) {
      setChips(message.chatOptions);
      applyInputLock(!!message.optionsOnly);
    }
  });
}

function buildPanelAiCommentHtml(aiComment = "", aiLoading = false) {
  return "";
}

function composeDoctorPanelBody(aiHtml, profileHtml) {
  const profile = profileHtml
    ? `<div class="nuvi-panel-profile-card">${profileHtml}</div>`
    : "";
  return `<div class="hero-doctor-panel-scroll">${aiHtml || ""}${profile}</div>`;
}

function setDoctorPanelAiState(doctorId, { aiComment = "", aiLoading = false } = {}) {
  const id = Number(doctorId);
  const comment = (aiComment || "").trim();
  if (comment) {
    doctorAiComments.set(id, comment);
    panelAiLoading = false;
  } else {
    panelAiLoading = !!aiLoading;
  }

  const body = document.getElementById("hero-doctor-panel-body");
  const panel = document.getElementById("hero-doctor-panel");
  const panelOpen = panel && !panel.hidden && panelDoctorId === id;

  if (!panelOpen || !body) {
    openDoctorSidePanel(id, { aiComment: comment, aiLoading: !comment && !!aiLoading });
    return;
  }

  const html = buildPanelAiCommentHtml(comment || doctorAiComments.get(id) || "", !comment && !!aiLoading);
  const scroll = body.querySelector(".hero-doctor-panel-scroll") || body;
  const existing = scroll.querySelector(".nuvi-panel-ai-card");
  if (!html) {
    existing?.remove();
    return;
  }

  if (existing) {
    existing.outerHTML = html;
  } else {
    const profileCard = scroll.querySelector(".nuvi-panel-profile-card");
    if (profileCard) profileCard.insertAdjacentHTML("beforebegin", html);
    else scroll.insertAdjacentHTML("afterbegin", html);
  }
  body.scrollTop = 0;
}

let doctorListBoxDoctors = [];
let doctorListBoxSortMode = "preference";
const selectedDoctorsForCall = new Set();

function addDoctorCards(doctors) {
  doctorListBoxDoctors = doctors.slice();
  selectedDoctorsForCall.clear();
  // Default: only the top-ranked doctor is pre-selected.
  if (doctors.length > 0) selectedDoctorsForCall.add(doctors[0].id);

  const msgs = document.getElementById("chat-messages");
  let box = msgs.querySelector(".doctor-list-box");
  if (box) box.remove();

  box = document.createElement("div");
  box.className = "doctor-list-box";
  box.innerHTML = `
    <div class="doctor-list-box-toolbar">
      <span class="doctor-list-box-title">🦷 Matched Doctors (${doctors.length})</span>
      <div class="doctor-list-box-sort-wrap">
        <select class="doctor-list-box-sort" aria-label="Sort doctors">
          <option value="preference">Best fit</option>
          <option value="distance">Distance</option>
        </select>
      </div>
    </div>
    <div class="doctor-list-box-items"></div>`;

  const sortEl = box.querySelector(".doctor-list-box-sort");
  sortEl.value = doctorListBoxSortMode;
  sortEl.onchange = () => {
    doctorListBoxSortMode = sortEl.value;
    rerenderDoctorListBox();
  };

  renderDoctorListItems(box.querySelector(".doctor-list-box-items"), doctors);
  msgs.appendChild(box);
  scrollChatToBottom();
  setTimeout(scrollChatToBottom, 120);
}

function rerenderDoctorListBox() {
  const msgs = document.getElementById("chat-messages");
  const box = msgs?.querySelector(".doctor-list-box");
  if (!box) return;
  const items = box.querySelector(".doctor-list-box-items");
  if (!items) return;

  let sorted = doctorListBoxDoctors.slice();
  if (doctorListBoxSortMode === "distance") {
    sorted.sort((a, b) => (a.distanceMiles ?? 999) - (b.distanceMiles ?? 999));
  }
  renderDoctorListItems(items, sorted);
}

function renderDoctorListItems(container, doctors) {
  container.innerHTML = "";
  doctors.forEach(d => {
    const card = document.createElement("button");
    card.type = "button";
    card.className = "nuvi-doctor-card" + (d.recommended ? " recommended" : "") + (d.isSponsored ? " sponsored" : "");
    card.dataset.doctorId = String(d.id);

    const checked = selectedDoctorsForCall.has(d.id) ? "checked" : "";
    const badges = [
      d.isSponsored ? '<div class="nuvi-sponsored-badge">Sponsored</div>' : "",
      d.recommended ? '<div class="nuvi-rec-badge">Best Match</div>' : ""
    ].filter(Boolean).join("");

    card.innerHTML = `
      ${badges}
      <div class="nuvi-doctor-card-row">
        <input type="checkbox" class="doctor-list-check" data-doctor-id="${d.id}" ${checked} />
        <div style="flex:1;min-width:0">
          <div class="nuvi-doctor-card-top">
            <div class="nuvi-doctor-avatar">${escapeHtml(d.avatarInitials)}</div>
            <div>
              <div class="nuvi-doctor-name">${escapeHtml(d.name)}</div>
              <div class="nuvi-doctor-spec">${escapeHtml(d.specialty)}</div>
              <div class="nuvi-doctor-loc">${escapeHtml(d.location)}</div>
              ${d.officePhoneNumber ? `<div class="nuvi-doctor-phone">${escapeHtml(d.officePhoneNumber)}</div>` : ""}
            </div>
            <div class="nuvi-match-score">
              <div class="nuvi-match-num">${d.matchScore}</div>
              <div class="nuvi-match-label">Fit</div>
            </div>
          </div>
          ${d.matchReason ? `<div class="nuvi-doctor-reason">${escapeHtml(d.matchReason)}</div>` : ""}
          <div class="nuvi-doctor-tag">${escapeHtml(d.tag || "")}</div>
        </div>
      </div>`;

    const cb = card.querySelector(".doctor-list-check");
    cb.onclick = (e) => {
      e.stopPropagation();
      if (cb.checked) selectedDoctorsForCall.add(d.id);
      else selectedDoctorsForCall.delete(d.id);
    };
    card.onclick = (e) => {
      if (e.target === cb) return;
      selectDoctor(d.id);
    };

    container.appendChild(card);
  });
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

  const acceptedInsurances = Array.isArray(data.acceptedInsurances) ? data.acceptedInsurances : [];
  const insuranceCarriers = Array.isArray(data.insuranceCarriers) ? data.insuranceCarriers : [];
  let insuranceHtml = "";
  if (acceptedInsurances.length > 0) {
    insuranceHtml = `<ul class="nuvi-profile-insurance-list">${acceptedInsurances.map((ins) => {
      const name = escapeHtml(ins.carrierName || "Insurance");
      const plans = Array.isArray(ins.plans) ? ins.plans.filter(Boolean) : [];
      const plansHtml = plans.length
        ? `<div class="nuvi-profile-insurance-plans">${plans.map((p) => escapeHtml(p)).join(" · ")}</div>`
        : "";
      return `<li><strong>${name}</strong>${plansHtml}</li>`;
    }).join("")}</ul>`;
  } else if (insuranceCarriers.length > 0) {
    insuranceHtml = `<p>${insuranceCarriers.map((c) => escapeHtml(c)).join(", ")}</p>`;
  } else {
    insuranceHtml = `<p>Insurance information not listed yet.</p>`;
  }

  const patientReviewsHtml = (data.reviews || []).length
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
    : "";

  const googleReviews = data.googleReviews || [];
  const googleReviewsHtml = googleReviews.length
    ? googleReviews.map((r) => {
        const meta = r.recommendation
          ? `<div class="${reviewAuthorClass} nuvi-profile-review-meta">${escapeHtml(r.recommendation)}</div>`
          : `<div class="${reviewAuthorClass} nuvi-profile-review-meta">Google review</div>`;
        return `
        <div class="${reviewClass}">
          <div class="${reviewStarsClass}">${renderStars(r.rating)}</div>
          <div class="${reviewTextClass}">"${escapeHtml(r.reviewText)}"</div>
          ${meta}
          <div class="${reviewAuthorClass}">— ${escapeHtml(r.reviewerName)}</div>
        </div>`;
      }).join("")
    : "";

  let reviewsHtml = "";
  if (googleReviewsHtml) {
    const sourceLabel = data.googleReviewsLive ? "Google reviews · live" : "Google reviews";
    reviewsHtml += `<div class="nuvi-google-reviews-block"><div class="nuvi-profile-review-source">${sourceLabel}</div>${googleReviewsHtml}</div>`;
  }
  if (patientReviewsHtml) {
    reviewsHtml += `<div class="nuvi-patient-reviews-block">${googleReviewsHtml ? `<div class="nuvi-profile-review-source">Patient reviews on NuviDoc</div>` : ""}${patientReviewsHtml}</div>`;
  }
  if (!reviewsHtml) {
    reviewsHtml = data.summaryOfReviews
      ? `<p>${escapeHtml(data.summaryOfReviews)}</p>`
      : "<p>No reviews available yet.</p>";
  }

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
    <div class="${sectionClass}">
      <h4>Accepted insurance</h4>
      ${insuranceHtml}
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

async function fetchDoctorProfile(doctorId, { liveGoogleReviews = false } = {}) {
  const qs = liveGoogleReviews ? "?liveGoogleReviews=true" : "";
  const res = await fetch(`/api/doctors/${doctorId}${qs}`, { credentials: "same-origin" });
  const data = await res.json().catch(() => ({}));
  if (!res.ok) return null;
  return data;
}

async function openDoctorSidePanel(doctorId, options = {}) {
  const wrap = document.getElementById("hero-chat-split-wrap");
  const panel = document.getElementById("hero-doctor-panel");
  const body = document.getElementById("hero-doctor-panel-body");
  const id = Number(doctorId);
  const aiComment = (options.aiComment || doctorAiComments.get(id) || "").trim();
  const aiLoading = !!options.aiLoading && !aiComment;
  if (aiComment) {
    doctorAiComments.set(id, aiComment);
    panelAiLoading = false;
  } else if (typeof options.aiLoading === "boolean") {
    panelAiLoading = aiLoading;
  } else if (!aiComment && panelAiLoading && panelDoctorId === id) {
    // keep existing loading flag while profile refreshes
  } else if (!aiComment) {
    panelAiLoading = false;
  }
  const showAiLoading = !aiComment && (aiLoading || panelAiLoading);
  panelDoctorId = id;

  if (!wrap || !panel || !body) {
    await addDoctorProfileInChat(doctorId);
    return;
  }

  highlightDoctorCard(doctorId);
  wrap.classList.add("is-split");
  document.getElementById("hero-section")?.classList.add("has-doctor-panel");
  panel.hidden = false;
  panel.setAttribute("aria-hidden", "false");

  const loadingAiHtml = buildPanelAiCommentHtml(aiComment, showAiLoading);
  body.innerHTML = composeDoctorPanelBody(
    loadingAiHtml,
    '<div class="hero-doctor-panel-loading" aria-label="Loading"><span class="nuvi-loading"><span></span><span></span><span></span></span></div>'
  );
  body.scrollTop = 0;

  try {
    const data = await fetchDoctorProfile(doctorId, { liveGoogleReviews: true });
    if (panelDoctorId !== id) return;
    if (!data) {
      body.innerHTML = composeDoctorPanelBody(
        buildPanelAiCommentHtml(aiComment, showAiLoading),
        '<div class="hero-doctor-panel-loading">Unable to load this doctor profile.</div>'
      );
      return;
    }
    const latestComment = (doctorAiComments.get(id) || aiComment || "").trim();
    const stillLoadingAi = !latestComment && (panelAiLoading || showAiLoading);
    body.innerHTML = composeDoctorPanelBody(
      buildPanelAiCommentHtml(latestComment, stillLoadingAi),
      buildDoctorProfileHtml(data, { panel: true })
    );
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
    body.scrollTop = 0;
  } catch {
    if (panelDoctorId !== id) return;
    body.innerHTML = composeDoctorPanelBody(
      buildPanelAiCommentHtml(aiComment, showAiLoading),
      '<div class="hero-doctor-panel-loading">Unable to load this doctor profile.</div>'
    );
  }
}

function closeDoctorSidePanel() {
  const wrap = document.getElementById("hero-chat-split-wrap");
  const panel = document.getElementById("hero-doctor-panel");
  const body = document.getElementById("hero-doctor-panel-body");
  wrap?.classList.remove("is-split");
  document.getElementById("hero-section")?.classList.remove("has-doctor-panel");
  if (panel) {
    panel.hidden = true;
    panel.setAttribute("aria-hidden", "true");
  }
  if (body) body.innerHTML = "";
  panelDoctorId = null;
  panelAiLoading = false;
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
      <div class="nuvi-profile-loading">Loading doctor profile &amp; Google reviews…</div>
    </div>`;
  msgs.appendChild(wrap);
  msgs.scrollTop = msgs.scrollHeight;

  const bubble = wrap.querySelector(".nuvi-profile-bubble");
  try {
    const data = await fetchDoctorProfile(doctorId, { liveGoogleReviews: true });
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

function removeMatchSearchLoadingMessage() {
  const msgs = document.getElementById("chat-messages");
  if (!msgs) return;
  msgs.querySelectorAll(".msg.ai").forEach(el => {
    if (el.querySelector(".nuvi-loading") || el.textContent.trim() === MATCH_SEARCH_LOADING_MESSAGE) {
      el.remove();
    }
  });
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
  const payload = { ...body };
  if (userLatitude != null && userLongitude != null) {
    payload.latitude = userLatitude;
    payload.longitude = userLongitude;
  }
  if (selectedDoctorsForCall.size > 0) {
    payload.selectedDoctorIds = Array.from(selectedDoctorsForCall);
  }
  const res = await fetch("/api/chat/message", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    credentials: "same-origin",
    body: JSON.stringify(payload)
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
    window.NuvidocPatientPush?.start({ joinPatient: true }).catch(() => {});
  }

  if (sessionKey && window.NuvidocPatientPush) {
    window.NuvidocPatientPush.joinSession(sessionKey).catch(() => {});
  }
  initCallResultChatPush();

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
  } else if (doctorIdNum != null) {
    // Doctor recommendation loading belongs on the detail panel — avoid chat scroll.
    setDoctorPanelAiState(doctorIdNum, { aiLoading: true });
  } else {
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
      if (doctorIdNum != null) {
        panelAiLoading = false;
        setDoctorPanelAiState(doctorIdNum, {
          aiComment: `Sorry — ${errText}. Please try again.`,
          aiLoading: false
        });
      } else {
        addMessage(`Sorry — ${errText}. Please try again.`, "ai");
      }
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

      // Show doctor cards; remove the "Searching…" loading bubble first.
      if (searchData.doctorCards?.length) {
        removeMatchSearchLoadingMessage();
        closeDoctorSidePanel();
        addDoctorCards(searchData.doctorCards);
        clearRecommendedDoctors();
      }

      addMessage(
        searchData.text ||
          "Above is the list of doctors I found that match your requirements.",
        "ai"
      );
      applyChatResponseState(searchData);
      scrollChatToBottom();
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
          panelAiLoading = false;
          openDoctorSidePanel(data.selectedDoctor.id);
        }
      }

      if (selectedDoctorId) markDoctorRecommended(selectedDoctorId);
      else if (data.selectedDoctor?.id) markDoctorRecommended(data.selectedDoctor.id);
    }

    applyChatResponseState(data);
  } catch {
    removeTyping();
    if (doctorIdNum != null) {
      panelAiLoading = false;
      setDoctorPanelAiState(doctorIdNum, {
        aiComment: "I'm having trouble connecting right now. Please try again.",
        aiLoading: false
      });
    } else {
      addMessage("I'm having trouble connecting right now. Please try again.", "ai");
    }
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
  // Hide chips immediately so the UI feels instant.
  const chipsEl = document.getElementById("quick-chips");
  if (chipsEl) chipsEl.style.display = "none";
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
