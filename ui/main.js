
import { cameraPhotoFormat, companions, live, lowerBound, parseManualCount, rating, usage, validAddress } from "./camera.js";

const invoke = window.__TAURI__.core.invoke;
const el = id => document.getElementById(id);
const text = (id, value) => { el(id).textContent = value; };
const visible = (id, show) => { el(id).hidden = !show; };
const number = value => new Intl.NumberFormat(locale).format(value);
const percent = (value, digits = 0) => new Intl.NumberFormat(locale, { style: "percent", maximumFractionDigits: digits, minimumFractionDigits: digits }).format(value);
const swatches = { Low: "success", Elevated: "warning", High: "error" };
const success = new Set(["UsbConnected", "WifiConnected", "PhotoOpened"]);
const failure = new Set(["CameraDisconnected", "OpenFailed", "ScanFailed", "PhotoOpenFailed"]);
const connectionStatuses = ["NoCameraDetected", "Scanning", "UsbCameraNotFound", "ReadingCamera", "OpeningPhoto", ...success, ...failure];
const languages = {
  ar: ["العربية", "ar-SA"], bn: ["বাংলা", "bn-BD"], zh: ["简体中文", "zh-CN"], arz: ["العربية المصرية", "ar-EG"],
  en: ["English", "en-US"], fr: ["Français", "fr-FR"], de: ["Deutsch", "de-DE"], hi: ["हिन्दी", "hi-IN"],
  id: ["Bahasa Indonesia", "id-ID"], ja: ["日本語", "ja-JP"], pcm: ["Naijíriá Píjin", "en-NG"], pt: ["Português (Brasil)", "pt-BR"],
  ru: ["Русский", "ru-RU"], es: ["Español", "es-ES"], uk: ["Українська", "uk-UA"], ur: ["اردو", "ur-PK"],
};
const localeCache = new Map();
const busySelects = new WeakSet();
let preferences;
let catalog;
let fallback;
let locale = "en-US";
let report = null;
let manual = null;
let devices = [];
let activeDevice = "";
let status = "NoCameraDetected";
let usbCheckedAt = null;
let helpOverride = null;
let busy = false;
let wifiBusy = false;

function t(key, ...args) {
  return (catalog[key] ?? fallback[key] ?? key).replace(/\{(\d+)(?::(N0|d|T))?\}/g, (_, i, format) => {
    if (format === "N0") return number(args[i]);
    if (format === "d") return new Date(args[i]).toLocaleDateString(locale);
    if (format === "T") return new Intl.DateTimeFormat(locale, { timeStyle: "medium" }).format(args[i]);
    return String(args[i] ?? "");
  });
}

function updateSelectAvailability(select) {
  select.disabled = select.options.length === 0 || busySelects.has(select);
}

function setSelectBusy(select, active) {
  if (active) busySelects.add(select);
  else busySelects.delete(select);
  updateSelectAvailability(select);
}

function setupSelect(select, options) {
  select.replaceChildren(...options.map(([value, label]) => {
    const option = document.createElement("option");
    option.value = value;
    option.textContent = label;
    return option;
  }));
  updateSelectAvailability(select);
}

function systemLanguage() {
  for (const tag of navigator.languages) {
    const language = tag.toLowerCase().split("-")[0];
    if (languages[language]) return language;
  }
  return "en";
}

async function setLanguage() {
  const code = preferences.language === "system" ? systemLanguage() : preferences.language;
  locale = languages[code][1];
  if (!localeCache.has(code)) localeCache.set(code, JSON.parse(await invoke("catalog", { language: code })));
  catalog = localeCache.get(code);
  document.documentElement.lang = code;
  document.documentElement.dir = ["ar", "arz", "ur"].includes(code) ? "rtl" : "ltr";
  for (const item of document.querySelectorAll("[data-tr]")) item.textContent = t(item.dataset.tr);
  setupSelect(el("language"), [["system", t("SystemOption")], ...Object.entries(languages).map(([key, [label]]) => [key, label])]);
  setupSelect(el("theme"), [["System", t("SystemOption")], ["Light", t("ThemeLight")], ["Dark", t("ThemeDark")]]);
  el("language").value = preferences.language;
  el("theme").value = preferences.theme;
  el("scale").setAttribute("aria-label", t("UsageScale"));
  text("version", t("Version", preferences.version));
  if (!el("manual-error").hidden) text("manual-error", t("InvalidCount"));
  render();
  renderPicker();
  await fitConnectionBar();
}

async function fitConnectionBar() {
  const dashboard = document.querySelector(".dashboard");
  const measure = dashboard.querySelector(".connection").cloneNode(true);
  measure.classList.add("connection-measure");
  measure.inert = true;
  measure.querySelector(".device-picker").remove();
  measure.querySelector(".usb-scan-time").remove();
  for (const item of measure.querySelectorAll("[id]")) item.removeAttribute("id");
  dashboard.append(measure);
  const label = measure.querySelector(".status-message");
  let widest = 0;
  for (const key of connectionStatuses) {
    label.textContent = t(key);
    widest = Math.max(widest, measure.getBoundingClientRect().width);
  }
  measure.remove();

  const padding = parseFloat(getComputedStyle(dashboard).getPropertyValue("--wide-padding"));
  const required = Math.max(820, Math.ceil(widest + 2 * padding + 20));
  const available = Math.floor(screen.availWidth - 24);
  const minWidth = available > 0 ? Math.min(required, available) : required;
  await invoke("fit_window", { minWidth });
}

function showStatus() {
  text("connection", t(status));
  const time = el("usb-scan-time");
  time.hidden = status !== "UsbCameraNotFound" || usbCheckedAt === null;
  if (!time.hidden) {
    time.dateTime = usbCheckedAt.toISOString();
    time.textContent = t("UsbLastChecked", usbCheckedAt);
  }
  const body = document.documentElement.style;
  const state = success.has(status) ? "success" : failure.has(status) ? "error" : "muted";
  const activeError = report?.reading.deviceErrorCode > 0;
  body.setProperty("--state-color", "var(--" + state + ")");
  const camera = state === "success" ? (activeError ? "medium" : "low") : state === "error" ? "high" : "muted";
  body.setProperty("--camera-color", "var(--" + camera + ")");
}

function setStatus(key) {
  status = key;
  showStatus();
}

function countText(value, bound) {
  return (bound && value > 0 ? "≤ " : "") + number(value);
}

function render() {
  const reading = report?.reading;
  const durability = rating(reading);
  const max = durability?.[0];
  const count = manual === null ? reading?.count : { value: manual, kind: "Mechanical", isUpperBound: false };
  const score = usage(count, max);
  const zone = score?.zone;
  const ratio = count?.isUpperBound ? Math.ceil((score?.ratio ?? 0) * 1000) / 1000 : Math.floor((score?.ratio ?? 0) * 1000) / 1000;
  const displayedPercent = score ? (count.isUpperBound && count.value > 0 ? "≤ " : "") + percent(ratio, 1) : "";
  const battery = reading?.battery == null ? "—" : percent(reading.battery / 100);
  text("model", !reading ? t("NoCameraConnected") : reading.model === "Unknown camera" ? t("UnknownCamera") : reading.model);
  el("model").dir = reading && reading.model !== "Unknown camera" ? "ltr" : "";
  for (const key of ["maker", "serial", "firmware"]) text(key, reading?.[key] ?? "—");
  text("battery", battery);
  text("source", !reading ? "—" : manual !== null ? t("ManualCount") : reading.source === "Wifi" ? "Wi-Fi" : reading.source.toUpperCase());
  visible("rating-row", Boolean(durability));
  if (durability) {
    const reference = new URL(durability[1]);
    el("rating-link").href = reference.href;
    el("rating-link").textContent = reference.hostname.replace(/^www\./, "");
    el("rating-link").setAttribute("aria-label", t("RatingSource") + " — " + reading.maker);
  } else el("rating-link").removeAttribute("href");
  text("count", count ? countText(count.value, count.isUpperBound) : "—");
  text("count-label", t(count?.kind === "Mechanical" ? "MechanicalCount" : count?.kind === "Image" ? "ImageCount" : "ReportedCount"));
  text("rating", max ? t("Rating", max) : t("NoRating"));
  text("percent", displayedPercent);
  text("zone", t(zone ? zone + "Usage" : reading ? "NoUsage" : "AwaitingCamera"));
  el("zone").parentElement.style.setProperty("--zone-color", "var(--" + (swatches[zone] ?? "muted") + ")");
  el("marker").style.setProperty("--ratio", Math.min(100, (score?.ratio ?? 0) * 100) + "%");
  visible("marker", Boolean(score));
  let note = t(score ? "UsageBenchmark" : !reading ? "UsageComparison" : count?.kind === "Unspecified" ? "UsageUnspecified"
    : count?.kind === "Image" ? "UsageImageCount" : !count ? live(reading) ? "NoUsbCount" : "PhotoCountUnavailable" : "UsageNeedsRating");
  if (count?.isUpperBound && count.value > 0) {
    note = t("CountRange", lowerBound(count), count.value);
    if (!max) note += " " + t("UsageNeedsRating");
    else if (!score) note += " " + t("RangeSpansZones");
  }
  text("usage-note", note);

  renderHistory(reading);
  renderIssues(reading, zone, displayedPercent, battery);
  const { mobile, desktop } = companions(reading);
  renderCompanionApps(mobile, desktop);
  const fuji = reading?.maker.toLowerCase().startsWith("fujifilm");
  const exact = reading?.count?.kind === "Mechanical" && (!reading.count.isUpperBound || reading.count.value === 0);
  const allowManual = Boolean((max || fuji) && !exact);
  const photoFormat = live(reading) && !reading.count ? cameraPhotoFormat(reading) : null;
  visible("manual-form", allowManual);
  visible("reset-count", manual !== null);
  text("help-title", t(!reading ? "ConnectTitle" : allowManual && (!photoFormat || mobile?.mechanical) ? "ManualTitle" : "RefreshTitle"));
  let help;
  if (helpOverride) help = t(...helpOverride);
  else if (!reading) help = t("ConnectHelp", t("Scan"), t("OpenPhoto")) + " " + t("PhotoFormatsHelp");
  else if (allowManual && mobile?.mechanical) help = t("FujifilmManualHelp");
  else if (reading.source === "Wifi") help = t("WifiRefresh", t("ScanWifi")) + (allowManual ? " " + t("ManualHelp") : "");
  else if (photoFormat) help = t("UsbPhotoHelp", photoFormat, t("Scan"), t("OpenPhoto")) + (allowManual ? " " + t("ManualHelp") : "");
  else help = allowManual ? t("ManualHelp") : t("RefreshHelp", t("Scan"), t("OpenPhoto"));
  text("help", help);
  showStatus();
}

function renderHistory(reading) {
  const count = reading?.count;
  visible("history", count?.property != null);
  if (count?.property == null) return;
  const comparison = report.comparison;
  const previous = comparison?.previousCount;
  const bounded = count.isUpperBound;
  const kind = bounded ? "Bound" : "Count";
  const format = value => bounded && value > 0 ? t("HistoryBoundValue", value) : number(value);
  const current = format(count.value);
  let message;
  if (!comparison) message = t(reading.serial?.trim() ? "NoHistory" : "HistoryNeedsSerial");
  else if (previous == null) message = t("First" + kind, current);
  else if (previous === count.value) message = t(kind + "Unchanged", current);
  else message = t(kind + (count.value < previous ? "Decreased" : "Increased"), format(previous), current);
  const decrease = comparison?.lastDecrease;
  if (previous != null && count.value >= previous && decrease) {
    message += "\n" + t("LastDecrease", format(decrease.From), format(decrease.To), decrease.ObservedAt);
  }
  el("history").style.color = decrease ? "var(--warning)" : "";
  text("history", message);
}

function paragraph(value, css) {
  const p = document.createElement("p");
  p.textContent = value;
  if (css) p.className = css;
  return p;
}

function renderIssues(reading, zone, displayedPercent, battery) {
  visible("issues", Boolean(reading));
  if (!reading) return;
  const list = el("issue-list");
  list.replaceChildren();
  function add(message, remedy = "", warning = false) {
    const row = document.createElement("div");
    row.append(paragraph(message, warning ? "problem" : "muted"));
    if (remedy) row.append(paragraph(remedy, "muted"));
    list.append(row);
  }
  const { deviceErrorCode: code, storageIssues, errorSource } = reading;
  if (code == null) add(t(storageIssues === 0 ? "CameraErrorStorageChecked" : "CameraErrorUnavailable"));
  else if (errorSource === "NikonDisplay" && code <= 1) add(t(code ? "CameraDisplayError" : "CameraDisplayErrorClear"), code ? t("FixUnknownCameraError") : "", Boolean(code));
  else if (errorSource === "SonyTemperature" && code <= 2) add(t(code ? "CameraTemperatureWarning" : "CameraTemperatureClear"), code ? t("FixCoolCamera") : "", Boolean(code));
  else if (code === 0) add(t("CameraErrorClear"));
  else {
    const num = String(code).padStart(2, "0");
    const hex = "0x" + code.toString(16).toUpperCase().padStart(8, "0");
    const label = "CanonError" + num;
    if (errorSource === "CanonEos" && Object.hasOwn(fallback, label)) {
      const fixes = { 1: "FixLensContacts", 2: "FixReseatStorage", 3: "FixStorageFolders", 4: "FixStorageFull", 5: "FixBuiltInFlash", 8: "FixTransmitterBattery", 40: "FixCameraPower", 60: "FixLensMovement" };
      add(t("CameraErrorMatch", num, t(label), hex), t("FixConfirmCanonCode", t(fixes[code] ?? "FixRestartCanon")), true);
    } else add(t("CameraErrorUnknown", hex), t("FixUnknownCameraError"), true);
  }
  const issues = [
    [1, "StorageFull", "FixStorageFull"], [2, "StorageReadOnly", "FixStorageReadOnly"],
    [4, "StorageUnavailable", "FixReseatStorage"], [8, "StorageBusy", "FixStorageBusy"],
    [16, "StorageUnformatted", "FixStorageUnformatted"], [32, "StorageAccessError", "FixReseatStorage"],
  ];
  for (const [bit, message, fix] of issues) {
    if (storageIssues & bit) add(t(message), t(fix), true);
  }
  if (reading.battery != null && reading.battery <= 20) add(t("IssueLowBattery", battery), t("FixChargeBattery"), true);
  if (zone === "High") add(t("IssueHighShutterUsage", displayedPercent), t("FixHighShutterUsage"), true);
  if (live(reading) && report.comparison?.lastDecrease) add(t("IssueShutterCountDecreased"), t("FixShutterCountDecreased"), true);
}

function appendLink(row, label, url, accessibleLabel = label) {
  if (!url) return;
  const link = document.createElement("a");
  link.href = url;
  link.textContent = label;
  link.setAttribute("aria-label", accessibleLabel);
  row.append(link);
}

function renderCompanionApps(mobile, desktop) {
  visible("companions", Boolean(mobile || desktop.length));
  const list = el("app-list");
  list.replaceChildren();
  if (mobile) {
    const row = document.createElement("div");
    row.className = "app-row";
    if (mobile.name) row.append(paragraph(mobile.name));
    appendLink(row, "App Store", mobile.apple);
    appendLink(row, "Google Play", mobile.google);
    appendLink(row, t("CheckAppCompatibility"), mobile.compatibility);
    list.append(row);
    if (!mobile.compatibility && !mobile.mechanical) list.append(paragraph(t("CompanionPhotoTransfer"), "muted"));
  }
  for (const app of desktop) {
    const row = document.createElement("div");
    row.className = "app-row";
    row.append(paragraph(app.name));
    const isMac = preferences.platform === "macOS";
    const url = isMac ? app.macOS : app.windows;
    if (url) appendLink(row, preferences.platform, url, app.name + " — " + preferences.platform);
    else row.append(paragraph(t("CompanionPlatformOnly", isMac ? "Windows" : "macOS"), "muted"));
    list.append(row);
  }
}

function resetManual() {
  manual = null;
  helpOverride = null;
  el("manual-count").value = "";
  visible("manual-error", false);
}

function applyReport(next, key) {
  report = next;
  status = key;
  resetManual();
  render();
}

function setBusy(value) {
  busy = value;
  for (const id of ["scan", "wifi", "photo"]) el(id).disabled = value;
  setSelectBusy(el("picker"), value);
}

function renderPicker() {
  const display = devices.length > 1 || (devices.length > 0 && !activeDevice);
  visible("picker-row", display);
  const options = devices.map(device => [device.id, device.name ?? t("UnknownCamera")]);
  if (!activeDevice && options.length) options.unshift(["", t("ConnectedCameras")]);
  setupSelect(el("picker"), options);
  el("picker").value = activeDevice;
}

async function readDevice(id) {
  activeDevice = id;
  renderPicker();
  applyReport(null, "ReadingCamera");
  try {
    applyReport(await invoke("read_usb", { id }), "UsbConnected");
  } catch (error) {
    if (error?.disconnected) {
      devices = devices.filter(device => device.id !== id);
      activeDevice = "";
      renderPicker();
      setStatus("CameraDisconnected");
    } else {
      helpOverride = ["OpenFailedHelp"];
      setStatus("OpenFailed");
      render();
    }
  }
}

el("scan").addEventListener("click", async () => {
  if (busy) return;
  setBusy(true);
  const previous = activeDevice;
  devices = [];
  activeDevice = "";
  renderPicker();
  applyReport(null, "Scanning");
  try {
    devices = await invoke("scan_usb");
    if (devices.length) {
      await readDevice((devices.find(device => device.id === previous) ?? devices[0]).id);
    } else {
      usbCheckedAt = new Date();
      setStatus("UsbCameraNotFound");
    }
  } catch {
    helpOverride = [preferences.platform === "Windows" ? "ScanFailedHelp" : "OpenFailedHelp"];
    setStatus("ScanFailed");
    render();
  } finally {
    setBusy(false);
  }
});

el("picker").addEventListener("change", async event => {
  if (busy || !event.target.value) return;
  setBusy(true);
  try { await readDevice(event.target.value); } finally { setBusy(false); }
});

el("photo").addEventListener("click", async () => {
  if (busy) return;
  setBusy(true);
  const oldStatus = status;
  setStatus("OpeningPhoto");
  try {
    const next = await invoke("open_photo");
    if (next) {
      activeDevice = "";
      renderPicker();
      applyReport(next, "PhotoOpened");
    }
    else setStatus(oldStatus);
  } catch {
    setStatus("PhotoOpenFailed");
    helpOverride = ["PhotoFormatsHelp"];
    render();
  } finally {
    setBusy(false);
  }
});

function setWifiBusy(value) {
  wifiBusy = value;
  for (const id of ["address", "discover", "connect-wifi"]) el(id).disabled = value;
  setSelectBusy(el("addresses"), value);
}

el("wifi").addEventListener("click", () => {
  if (busy || wifiBusy) return;
  setupSelect(el("addresses"), []);
  text("wifi-status", "");
  el("wifi-dialog").showModal();
  el("discover").click();
});

el("close-wifi").addEventListener("click", () => el("wifi-dialog").close());
el("wifi-dialog").addEventListener("close", () => {
  if (wifiBusy) invoke("cancel_wifi").catch(console.error);
});

el("discover").addEventListener("click", async () => {
  if (wifiBusy) return;
  setWifiBusy(true);
  text("wifi-status", t("WifiSearching"));
  try {
    const addresses = await invoke("scan_wifi");
    setupSelect(el("addresses"), addresses.map(address => [address, address]));
    if (addresses.length && !el("address").value.trim()) el("address").value = addresses[0];
    text("wifi-status", addresses.length ? "" : t("WifiNone"));
  } catch (error) {
    if (!error?.cancelled) text("wifi-status", t("WifiFailed"));
  } finally {
    setWifiBusy(false);
  }
});

el("addresses").addEventListener("change", () => {
  el("address").value = el("addresses").value;
});

el("wifi-form").addEventListener("submit", async event => {
  event.preventDefault();
  if (wifiBusy) return;
  const address = el("address").value.trim();
  if (!validAddress(address)) {
    text("wifi-status", t("WifiInvalidAddress"));
    return;
  }
  setWifiBusy(true);
  setBusy(true);
  text("wifi-status", t("WifiPairing"));
  try {
    const response = await invoke("read_wifi", { address });
    if (el("wifi-dialog").open) {
      devices = [];
      activeDevice = "";
      renderPicker();
      applyReport(response, "WifiConnected");
      setWifiBusy(false);
      el("wifi-dialog").close();
    }
  } catch (error) {
    if (!error?.cancelled) text("wifi-status", t("WifiFailed"));
  } finally {
    setBusy(false);
    setWifiBusy(false);
  }
});

el("manual-form").addEventListener("submit", event => {
  event.preventDefault();
  const value = parseManualCount(el("manual-count").value);
  visible("manual-error", value === null);
  if (value === null) {
    text("manual-error", t("InvalidCount"));
    return;
  }
  manual = value;
  helpOverride = null;
  render();
});

el("reset-count").addEventListener("click", () => {
  resetManual();
  render();
});

el("settings").addEventListener("click", () => {
  visible("settings-error", false);
  el("settings-dialog").showModal();
});

function settingsError(error) {
  visible("settings-error", true);
  text("settings-error", String(error));
}

el("language").addEventListener("change", async event => {
  const choice = event.target;
  const language = choice.value;
  setSelectBusy(choice, true);
  try {
    await invoke("save_language", { language });
    preferences.language = language;
    await setLanguage();
  } catch (error) {
    choice.value = preferences.language;
    settingsError(error);
  } finally {
    setSelectBusy(choice, false);
  }
});

el("theme").addEventListener("change", async event => {
  const choice = event.target;
  const theme = choice.value;
  setSelectBusy(choice, true);
  try {
    await invoke("save_theme", { theme });
    preferences.theme = theme;
    document.documentElement.dataset.theme = theme;
  } catch (error) {
    choice.value = preferences.theme;
    settingsError(error);
  } finally {
    setSelectBusy(choice, false);
  }
});

document.addEventListener("click", async event => {
  const link = event.target.closest("a[href]");
  if (!link) return;
  event.preventDefault();
  try { await invoke("open_link", { url: link.href }); }
  catch (error) {
    if (el("settings-dialog").open) settingsError(error);
    else {
      helpOverride = ["BrowserFailed", link.href];
      render();
    }
  }
});

try {
  [preferences, fallback] = await Promise.all([invoke("settings"), invoke("catalog", { language: "en" }).then(JSON.parse)]);
  localeCache.set("en", fallback);
  document.documentElement.dataset.theme = preferences.theme;
  await setLanguage();
  await invoke("ui_ready");
  if (!preferences.smoke) el("scan").click();
} catch (error) {
  document.body.textContent = String(error);
  console.error(error);
}
