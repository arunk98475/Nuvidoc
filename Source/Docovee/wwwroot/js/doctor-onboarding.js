let sessionKey = null;
let usePasswordInput = false;

const branding = window.nuvidocBranding || { siteName: "NuviDoc", chatBotName: "Nuvi" };
const BOT_AVATAR = branding.chatBotName;

document.addEventListener("DOMContentLoaded", () => {
  const chatInput = document.getElementById("chat-input");
  if (chatInput?.tagName === "TEXTAREA") autoResize(chatInput);
  startOnboarding();
});

async function startOnboarding() {
  document.getElementById("send-btn").disabled = true;
  showTyping();
  try {
    const data = await postMessage("");
    removeTyping();
    handleResponse(data);
  } catch {
    removeTyping();
    addMessage("I'm having trouble connecting right now. Please refresh and try again.", "ai");
  }
  document.getElementById("send-btn").disabled = false;
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
  const sendBtn = document.getElementById("send-btn");

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
    area.insertBefore(newInput, sendBtn);
    return newInput;
  }

  if (!passwordMode && input.tagName === "INPUT") {
    const newTa = document.createElement("textarea");
    newTa.id = "chat-input";
    newTa.className = "chat-input";
    newTa.rows = 1;
    newTa.name = "chat-message";
    newTa.autocomplete = "off";
    newTa.placeholder = "Type your answer...";
    newTa.value = value;
    newTa.onkeydown = handleKey;
    newTa.oninput = function () { autoResize(this); };
    input.replaceWith(newTa);
    area.insertBefore(newTa, sendBtn);
    autoResize(newTa);
    return newTa;
  }

  return input;
}

function updateInputMode(passwordMode) {
  usePasswordInput = passwordMode;
  const input = ensureChatInputElement(passwordMode);
  if (!input) return;

  if (input.tagName === "INPUT") {
    input.type = passwordMode ? "password" : "text";
    input.name = passwordMode ? "new-password" : "chat-message";
    input.autocomplete = passwordMode ? "new-password" : "off";
    input.placeholder = passwordMode ? "Enter your password..." : "Type your answer...";
  }
}

function addMessage(text, role) {
  const msgs = document.getElementById("chat-messages");
  const div = document.createElement("div");
  div.className = `msg ${role}`;
  const bubbleContent = formatMessage(text);
  div.innerHTML = `
    <div class="msg-avatar">${role === "ai" ? BOT_AVATAR : "You"}</div>
    <div class="msg-bubble">${bubbleContent}</div>`;
  msgs.appendChild(div);
  msgs.scrollTop = msgs.scrollHeight;
}

function formatMessage(text) {
  return escapeHtml(text)
    .replace(/\*\*(.+?)\*\*/g, "<strong>$1</strong>")
    .replace(/_(.+?)_/g, "<em>$1</em>")
    .replace(/\n/g, "<br>");
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
    btn.type = "button";
    btn.className = "chip";
    btn.textContent = opt;
    btn.onclick = () => sendChip(btn);
    chipsEl.appendChild(btn);
  });
}

function updateProgress(questionNumber, totalQuestions, profileCompletionPercent) {
  const wrap = document.getElementById("onboarding-progress");
  const bar = document.getElementById("onboarding-progress-bar");
  const text = document.getElementById("onboarding-progress-text");
  const pct = profileCompletionPercent ?? (questionNumber && totalQuestions
    ? Math.round((questionNumber / totalQuestions) * 100)
    : null);

  if (pct == null) {
    wrap.hidden = true;
    return;
  }

  wrap.hidden = false;
  bar.style.width = `${pct}%`;
  if (questionNumber && totalQuestions) {
    text.textContent = `Profile ${pct}% — Question ${questionNumber} of ${totalQuestions}`;
  } else {
    text.textContent = `Profile ${pct}% complete`;
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
  div.innerHTML = `<div class="msg-avatar">${BOT_AVATAR}</div><div class="msg-bubble"><span class="nuvi-loading"><span></span><span></span><span></span></span></div>`;
  msgs.appendChild(div);
  msgs.scrollTop = msgs.scrollHeight;
}

function removeTyping() {
  document.getElementById("typing-msg")?.remove();
}

async function postMessage(text) {
  const res = await fetch("/api/doctor-onboarding/message", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    credentials: "same-origin",
    body: JSON.stringify({ sessionKey, message: text })
  });
  const data = await res.json().catch(() => ({}));
  if (!res.ok) {
    throw new Error(data.message || data.detail || `Server error (${res.status})`);
  }
  return data;
}

function handleResponse(data) {
  if (data.text) addMessage(data.text, "ai");
  sessionKey = data.sessionKey;
  updateInputMode(data.usePasswordInput);
  setChips(data.options);
  updateProgress(data.questionNumber, data.totalQuestions, data.profileCompletionPercent);

  if (data.flowComplete) {
    setChips([]);
    const input = document.getElementById("chat-input");
    input.disabled = true;
    document.getElementById("send-btn").disabled = true;
    if (data.signedIn) {
      setTimeout(() => { window.location.href = "/Account/DoctorProfile"; }, 2000);
    }
  }
}

async function sendMessage() {
  const input = document.getElementById("chat-input");
  const text = input.value.trim();
  if (!text) return;

  const wasPasswordInput = usePasswordInput;
  input.value = "";
  if (input.tagName === "TEXTAREA") autoResize(input);
  document.getElementById("send-btn").disabled = true;

  addMessage(wasPasswordInput ? "••••••••" : text, "user");
  showTyping();

  try {
    const data = await postMessage(text);
    removeTyping();
    handleResponse(data);
  } catch {
    removeTyping();
    addMessage("Sorry — something went wrong. Please try again.", "ai");
  }

  document.getElementById("send-btn").disabled = false;
}

function sendChip(btn) {
  document.getElementById("chat-input").value = btn.textContent;
  sendMessage();
}

function handleKey(e) {
  if (e.key === "Enter" && !e.shiftKey) {
    e.preventDefault();
    sendMessage();
  }
}
