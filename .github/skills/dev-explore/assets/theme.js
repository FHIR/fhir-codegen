(() => {
  "use strict";

  const root = document.documentElement;
  const choices = new Set(["system", "light", "dark"]);
  const siteKey = root.dataset.themeKey;
  const canPersist = Boolean(siteKey && !siteKey.includes("{TBD:"));
  const storageKey = `dev-explore:theme:${siteKey}`;
  let storage = null;
  let preference = "system";
  let storageMessage = "";

  function storageFailed(error) {
    storageMessage =
      "Appearance applies to this page, but this browser could not save " +
      "or load your preference.";
    console.warn("Documentation theme storage is unavailable.", error);
  }

  if (!canPersist) {
    storageMessage =
      "This preview has no site identifier. Appearance will not be saved.";
    console.warn("Set a unique data-theme-key before publishing documentation.");
  } else {
    try {
      storage = window.localStorage;
      const saved = storage.getItem(storageKey);
      if (saved !== null && !choices.has(saved)) {
        storageMessage =
          "An unrecognized saved appearance was ignored. Choose it again.";
        console.warn("Unrecognized saved documentation theme.");
      } else if (saved !== null) {
        preference = saved;
      }
    } catch (error) {
      storageFailed(error);
    }
  }

  root.dataset.theme = preference;

  function initialize() {
    const control = document.getElementById("theme-control");
    const selector = document.getElementById("theme-choice");
    const status = document.getElementById("theme-status");
    if (!control || !selector || !status) {
      console.error("The documentation theme controls are incomplete.");
      return;
    }

    function showStatus() {
      status.textContent = storageMessage;
      status.hidden = !storageMessage;
    }

    function apply(value) {
      preference = value;
      root.dataset.theme = value;
      selector.value = value;
    }

    apply(preference);
    control.hidden = false;
    showStatus();

    selector.addEventListener("change", () => {
      if (!choices.has(selector.value)) {
        console.error("Unrecognized documentation theme selection.");
        selector.value = preference;
        return;
      }
      apply(selector.value);
      if (canPersist) {
        try {
          storage = window.localStorage;
          storage.setItem(storageKey, preference);
          storageMessage = "";
        } catch (error) {
          storageFailed(error);
        }
      }
      showStatus();
    });

    window.addEventListener("storage", (event) => {
      if (!canPersist || event.storageArea !== storage ||
          (event.key !== storageKey && event.key !== null)) {
        return;
      }
      const value = event.newValue === null ? "system" : event.newValue;
      if (!choices.has(value)) {
        storageMessage =
          "Another page saved an unrecognized appearance. Choose it again.";
        console.warn("Unrecognized documentation theme in a storage event.");
        showStatus();
        return;
      }
      apply(value);
      storageMessage = "";
      showStatus();
    });
  }

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", initialize, { once: true });
  } else {
    initialize();
  }
})();
