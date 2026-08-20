(function () {
  const cfg = window.NuvidocDoctorBilling || {};

  function escapeHtml(value) {
    return String(value == null ? "" : value)
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;");
  }

  async function sponsorshipApi(path, options) {
    const res = await fetch(path, Object.assign({
      credentials: "same-origin",
      headers: { Accept: "application/json" }
    }, options || {}));
    const data = await res.json().catch(function () { return {}; });
    if (!res.ok) throw new Error(data.message || "Request failed.");
    return data;
  }

  function renderSponsorship(status) {
    const checkbox = document.getElementById("dp-sponsor-enable");
    const scoreEl = document.getElementById("dp-sponsor-score");
    const minEl = document.getElementById("dp-sponsor-min");
    const reviewsEl = document.getElementById("dp-sponsor-reviews");
    const reviewsMinEl = document.getElementById("dp-sponsor-reviews-min");
    const cardStatusEl = document.getElementById("dp-sponsor-card-status");
    const requirements = document.getElementById("dp-sponsor-requirements");
    const billingSummary = document.getElementById("dp-sponsor-billing-summary");
    const bar = document.getElementById("dp-sponsor-bar");
    const paused = document.getElementById("dp-sponsor-paused");
    const errorEl = document.getElementById("dp-sponsor-error");
    const tips = document.getElementById("dp-sponsor-tips");
    if (!checkbox || !status) return;

    checkbox.checked = !!status.enabled;
    checkbox.disabled = !status.canEnable && !status.enabled;
    if (scoreEl) scoreEl.textContent = String(status.qualityScore ?? 0);
    if (minEl) minEl.textContent = String(status.minRequired ?? 40);
    if (reviewsEl) reviewsEl.textContent = String(status.googleReviewCount ?? 0);
    if (reviewsMinEl) reviewsMinEl.textContent = String(status.minGoogleReviewsRequired ?? 0);
    if (cardStatusEl) {
      cardStatusEl.textContent = status.hasPaymentMethod
        ? "On file"
        : "Required — add a card below";
    }
    if (requirements) {
      const items = requirements.querySelectorAll("li");
      if (items[0]) items[0].className = status.meetsQualityRequirement ? "is-met" : "is-unmet";
      if (items[1]) items[1].className = status.meetsGoogleReviewRequirement ? "is-met" : "is-unmet";
      if (items[2]) items[2].className = status.hasPaymentMethod ? "is-met" : "is-unmet";
    }
    if (billingSummary && status.sponsorshipBillingSummary) {
      billingSummary.textContent = status.sponsorshipBillingSummary;
    }
    if (bar) bar.style.width = Math.max(0, Math.min(100, status.qualityScore || 0)) + "%";
    if (paused) {
      paused.hidden = !status.paused;
      if (status.pausedMessage) paused.textContent = status.pausedMessage;
    }
    if (errorEl) errorEl.hidden = true;
    if (tips && Array.isArray(status.tips)) {
      tips.innerHTML = status.tips.map(function (t) { return "<li>" + escapeHtml(t) + "</li>"; }).join("");
    }
  }

  async function refreshSponsorshipStatus() {
    try {
      const status = await sponsorshipApi("/api/doctor/billing/sponsorship");
      renderSponsorship(status);
    } catch (_) { /* keep current UI */ }
  }

  async function initSponsorship() {
    const checkbox = document.getElementById("dp-sponsor-enable");
    if (!checkbox) return;

    try {
      const status = await sponsorshipApi("/api/doctor/billing/sponsorship");
      renderSponsorship(status);
    } catch (err) {
      /* keep server-rendered values */
    }

    checkbox.addEventListener("change", async function () {
      const errorEl = document.getElementById("dp-sponsor-error");
      const desired = checkbox.checked;
      checkbox.disabled = true;
      try {
        await sponsorshipApi("/api/doctor/billing/sponsorship", {
          method: "PUT",
          headers: { "Content-Type": "application/json", Accept: "application/json" },
          body: JSON.stringify({ enabled: desired })
        });
        const status = await sponsorshipApi("/api/doctor/billing/sponsorship");
        renderSponsorship(status);
      } catch (err) {
        checkbox.checked = !desired;
        if (errorEl) {
          errorEl.hidden = false;
          errorEl.textContent = err.message || "Could not update sponsorship.";
        }
        checkbox.disabled = !desired && checkbox.disabled;
        try {
          const status = await sponsorshipApi("/api/doctor/billing/sponsorship");
          renderSponsorship(status);
        } catch (_) { /* ignore */ }
      }
    });
  }

  initSponsorship();

  if (!cfg.publishableKey || typeof Stripe === "undefined") return;


  const tabs = document.querySelectorAll("[data-billing-tab]");
  const panels = document.querySelectorAll("[data-billing-panel]");
  const pmList = document.getElementById("dp-billing-pm-list");
  const pmEmpty = document.getElementById("dp-billing-pm-empty");
  const addCardBtn = document.getElementById("dp-billing-add-card");
  const modal = document.getElementById("dp-billing-card-modal");
  const cardMount = document.getElementById("dp-billing-card-element");
  const cardError = document.getElementById("dp-billing-card-error");
  const cardSave = document.getElementById("dp-billing-card-save");
  const cardCancel = document.getElementById("dp-billing-card-cancel");
  const modalClose = document.getElementById("dp-billing-modal-close");
  const yearSelect = document.getElementById("dp-billing-year");
  const invoiceList = document.getElementById("dp-billing-invoice-list");
  const invoiceEmpty = document.getElementById("dp-billing-invoice-empty");
  const recentList = document.getElementById("dp-billing-recent-charges");
  const contactView = document.getElementById("dp-billing-contact-view");
  const contactForm = document.getElementById("dp-billing-contact-form");
  const editContactBtn = document.getElementById("dp-billing-edit-contact");
  const cancelContactBtn = document.getElementById("dp-billing-contact-cancel");

  const stripe = Stripe(cfg.publishableKey);
  let elements = null;
  let paymentElement = null;
  let setupClientSecret = null;

  tabs.forEach(function (tab) {
    tab.addEventListener("click", function () {
      const name = tab.getAttribute("data-billing-tab");
      tabs.forEach(function (t) {
        const active = t === tab;
        t.classList.toggle("is-active", active);
        t.setAttribute("aria-selected", active ? "true" : "false");
      });
      panels.forEach(function (panel) {
        const show = panel.getAttribute("data-billing-panel") === name;
        panel.classList.toggle("is-active", show);
        panel.hidden = !show;
      });
      if (name === "invoices") loadInvoices();
    });
  });

  function formatMoney(cents, currency) {
    const amount = (cents || 0) / 100;
    return new Intl.NumberFormat(undefined, {
      style: "currency",
      currency: (currency || "usd").toUpperCase()
    }).format(amount);
  }

  function statusBadge(status) {
    const s = String(status || "").toLowerCase();
    if (s === "succeeded") return { label: "Paid", cls: "paid" };
    if (s === "failed") return { label: "Failed", cls: "failed" };
    if (s === "skipped") return { label: "No fee", cls: "skipped" };
    return { label: "Pending", cls: "pending" };
  }

  function brandLabel(brand) {
    if (!brand) return "Card";
    return brand.charAt(0).toUpperCase() + brand.slice(1);
  }

  async function api(path, options) {
    const res = await fetch(path, Object.assign({
      credentials: "same-origin",
      headers: { Accept: "application/json" }
    }, options || {}));
    const data = await res.json().catch(function () { return {}; });
    if (!res.ok) throw new Error(data.message || "Request failed.");
    return data;
  }

  function renderPaymentMethods(methods) {
    if (!pmList) return;
    pmList.replaceChildren();
    const list = methods || [];
    if (pmEmpty) pmEmpty.hidden = list.length > 0;
    list.forEach(function (m) {
      const li = document.createElement("li");
      li.className = "dp-billing-pm-item";
      li.innerHTML =
        "<div class=\"dp-billing-pm-main\">" +
        "<span class=\"dp-billing-pm-brand\">" + brandLabel(m.brand) + "</span>" +
        "<span> ending in " + escapeHtml(m.last4) + "</span>" +
        (m.isDefault ? "<span class=\"dp-billing-pm-default\">Default</span>" : "") +
        "</div>" +
        "<div class=\"dp-billing-pm-actions\">" +
        (!m.isDefault ? "<button type=\"button\" class=\"dp-btn-link\" data-set-default=\"" + escapeHtml(m.id) + "\">Make default</button>" : "") +
        "<button type=\"button\" class=\"dp-btn-link dp-billing-pm-remove\" data-remove=\"" + escapeHtml(m.id) + "\">Remove</button>" +
        "</div>";
      pmList.appendChild(li);
    });
  }

  function renderChargeList(target, charges, limit) {
    if (!target) return;
    target.replaceChildren();
    const rows = (charges || []).slice(0, limit || 999);
    if (!rows.length) {
      const li = document.createElement("li");
      li.className = "dp-billing-empty-row";
      li.textContent = "No charges yet.";
      target.appendChild(li);
      return;
    }
    rows.forEach(function (c) {
      const badge = statusBadge(c.status);
      const when = new Date(c.chargedAt || c.createdAt);
      const kind = String(c.chargeKind || "").toLowerCase();
      const title = kind === "sponsorship"
        ? (c.patientName || "Sponsorship")
        : (c.patientName || "Visit");
      const li = document.createElement("li");
      li.className = "dp-billing-invoice-item";
      li.innerHTML =
        "<div class=\"dp-billing-invoice-main\">" +
        "<div class=\"dp-billing-invoice-title\">" + escapeHtml(title) + "</div>" +
        "<div class=\"dp-billing-invoice-sub\">" + when.toLocaleDateString(undefined, { month: "short", day: "numeric", year: "numeric" }) +
        " · " + formatMoney(c.amountCents, c.currency) + "</div>" +
        (c.failureMessage ? "<div class=\"dp-billing-invoice-fail\">" + escapeHtml(c.failureMessage) + "</div>" : "") +
        "</div>" +
        "<span class=\"dp-billing-badge " + badge.cls + "\">" + badge.label + "</span>";
      target.appendChild(li);
    });
  }

  async function loadPaymentMethods() {
    const methods = await api("/api/doctor/billing/payment-methods");
    renderPaymentMethods(methods);
  }

  async function loadInvoices() {
    const year = yearSelect ? parseInt(yearSelect.value, 10) : new Date().getFullYear();
    const charges = await api("/api/doctor/billing/charges?year=" + year);
    if (invoiceEmpty) invoiceEmpty.hidden = charges.length > 0;
    renderChargeList(invoiceList, charges);
  }

  async function loadRecentCharges() {
    const year = new Date().getFullYear();
    const charges = await api("/api/doctor/billing/charges?year=" + year);
    renderChargeList(recentList, charges, 6);
  }

  function initYearSelect() {
    if (!yearSelect) return;
    const current = new Date().getFullYear();
    for (let y = current; y >= current - 3; y--) {
      const opt = document.createElement("option");
      opt.value = String(y);
      opt.textContent = String(y);
      yearSelect.appendChild(opt);
    }
    yearSelect.addEventListener("change", loadInvoices);
  }

  async function openCardModal() {
    if (!modal || !cardMount) return;
    cardError.hidden = true;
    setupClientSecret = null;
    const result = await api("/api/doctor/billing/setup-intent", { method: "POST" });
    setupClientSecret = result.clientSecret;
    elements = stripe.elements({ clientSecret: setupClientSecret });
    paymentElement = elements.create("payment");
    cardMount.replaceChildren();
    paymentElement.mount(cardMount);
    modal.showModal();
  }

  function closeCardModal() {
    if (!modal) return;
    if (paymentElement) {
      paymentElement.unmount();
      paymentElement = null;
    }
    elements = null;
    setupClientSecret = null;
    modal.close();
  }

  async function saveCard() {
    if (!setupClientSecret) return;
    cardSave.disabled = true;
    cardError.hidden = true;
    try {
      const result = await stripe.confirmSetup({
        elements: elements,
        redirect: "if_required"
      });
      if (result.error) throw new Error(result.error.message || "Could not save card.");
      closeCardModal();
      await loadPaymentMethods();
      await loadRecentCharges();
      await refreshSponsorshipStatus();
    } catch (err) {
      cardError.textContent = err.message || "Could not save card.";
      cardError.hidden = false;
    } finally {
      cardSave.disabled = false;
    }
  }

  pmList?.addEventListener("click", async function (e) {
    const setDefault = e.target.closest("[data-set-default]");
    const remove = e.target.closest("[data-remove]");
    try {
      if (setDefault) {
        await api("/api/doctor/billing/payment-methods/default", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ paymentMethodId: setDefault.getAttribute("data-set-default") })
        });
        await loadPaymentMethods();
        await refreshSponsorshipStatus();
      }
      if (remove) {
        if (!confirm("Remove this card?")) return;
        await api("/api/doctor/billing/payment-methods/" + encodeURIComponent(remove.getAttribute("data-remove")), {
          method: "DELETE"
        });
        await loadPaymentMethods();
        await refreshSponsorshipStatus();
      }
    } catch (err) {
      alert(err.message || "Action failed.");
    }
  });

  addCardBtn?.addEventListener("click", function () {
    openCardModal().catch(function (err) { alert(err.message || "Could not start card setup."); });
  });
  cardSave?.addEventListener("click", saveCard);
  cardCancel?.addEventListener("click", closeCardModal);
  modalClose?.addEventListener("click", closeCardModal);

  editContactBtn?.addEventListener("click", function () {
    contactView.hidden = true;
    contactForm.hidden = false;
  });
  cancelContactBtn?.addEventListener("click", function () {
    contactForm.hidden = true;
    contactView.hidden = false;
  });

  contactForm?.addEventListener("submit", async function (e) {
    e.preventDefault();
    const fd = new FormData(contactForm);
    try {
      await api("/api/doctor/billing/contact", {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          billingEmail: fd.get("billingEmail"),
          line1: fd.get("line1"),
          line2: fd.get("line2"),
          city: fd.get("city"),
          state: fd.get("state"),
          postalCode: fd.get("postalCode"),
          country: "US"
        })
      });
      const contact = await api("/api/doctor/billing/contact");
      document.getElementById("dp-billing-contact-email").textContent = contact.billingEmail || "Not set";
      const parts = [contact.line1, contact.line2, contact.city, contact.state, contact.postalCode].filter(Boolean);
      document.getElementById("dp-billing-contact-address").textContent = parts.length ? parts.join(", ") : "Not set";
      contactForm.hidden = true;
      contactView.hidden = false;
    } catch (err) {
      alert(err.message || "Could not save contact.");
    }
  });

  initYearSelect();
  loadPaymentMethods().catch(function () { /* optional */ });
  loadRecentCharges().catch(function () { /* optional */ });
})();
