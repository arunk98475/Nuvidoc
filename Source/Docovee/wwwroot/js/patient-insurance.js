(function () {
  const catalog = window.nuvidocInsuranceCatalog || [];
  const selections = window.nuvidocInsuranceSelections || {};

  function plansForCarrier(carrierId) {
    if (!carrierId) return [];
    const carrier = catalog.find((c) => String(c.id) === String(carrierId));
    return carrier?.plans || [];
  }

  function syncPlanSelect(planSelect, carrierSelect, selectedPlanId) {
    if (!planSelect || !carrierSelect) return;

    const carrierId = carrierSelect.value;
    const plans = plansForCarrier(carrierId);
    const current = selectedPlanId ?? planSelect.value;

    planSelect.innerHTML = "";
    const empty = document.createElement("option");
    empty.value = "";
    empty.textContent = "Plan (optional)";
    planSelect.appendChild(empty);

    plans.forEach((plan) => {
      const opt = document.createElement("option");
      opt.value = String(plan.id);
      opt.textContent = plan.name;
      if (String(plan.id) === String(current)) opt.selected = true;
      planSelect.appendChild(opt);
    });

    if (!plans.some((p) => String(p.id) === String(current))) {
      planSelect.value = "";
    }
  }

  document.querySelectorAll(".ins-carrier-select").forEach((carrierSelect) => {
    const planId = carrierSelect.getAttribute("data-plan-target");
    const planSelect = planId ? document.getElementById(planId) : null;
    const initialPlanId = selections[planId];

    syncPlanSelect(planSelect, carrierSelect, initialPlanId);

    carrierSelect.addEventListener("change", () => {
      syncPlanSelect(planSelect, carrierSelect, null);
    });
  });
})();
