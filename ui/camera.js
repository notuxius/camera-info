
import { apps, desktopRules, mobileRules, ratings, sonyAliases } from "./catalog.js";

const canonAliases = {
  EOSREBELT8I: "EOS850D", EOSKISSX10I: "EOS850D", EOS6DM2: "EOS6DMARKII",
  EOS5RM2: "EOSR5MARKII", EOSR5M2: "EOSR5MARKII", EOS5RMARKII: "EOSR5MARKII",
  EOSR6M2: "EOSR6MARKII", EOSR6M3: "EOSR6MARKIII", EOSM50M2: "EOSM50MARKII", EOSKISSM2: "EOSM50MARKII",
};
const lumixNames = { S1II: "S1M2", S1IIE: "S1M2ES", S1RII: "S1RM2", S5II: "S5M2", S5IIX: "S5M2X", G9II: "G9M2", GH5II: "GH5M2" };
const strip = (text, prefix) => text.startsWith(prefix) ? text.slice(prefix.length) : text;
const key = text => text.toUpperCase().replace(/[\s_-]/gu, "").replaceAll("Α", "A");
const canon = model => {
  const name = strip(model.toUpperCase().replace(/[^A-Z0-9]/g, ""), "CANON");
  return canonAliases[name] ?? name;
};

export const live = reading => reading?.source === "Usb" || reading?.source === "Wifi";

export function rating(reading) {
  if (!reading) return null;
  return ratings[canon(reading.model)] ?? ratings[reading.model] ?? null;
}

export function companions(reading) {
  if (!reading) return { mobile: null, desktop: [] };
  const manufacturer = reading.maker.trim().toUpperCase();
  let brand = manufacturer.split(" ")[0];
  let model = key(reading.model);
  if (brand === "RICOH" && model.startsWith("PENTAX")) brand = "PENTAX";
  if (brand === "OM" && (manufacturer === "OM SYSTEM" || manufacturer.startsWith("OM DIGITAL SOLUTIONS"))) brand = "OMSYSTEM";

  if (brand === "CANON") model = canon(reading.model);
  else if (brand === "NIKON") model = strip(model, "NIKON").replace(/^DSC/, "");
  else if (brand === "PANASONIC") model = strip(strip(model, "PANASONIC"), "LUMIX");
  else if (brand === "OMSYSTEM" || brand === "OLYMPUS") model = strip(strip(strip(model, "OMSYSTEM"), "OLYMPUS"), "OMD");
  else model = strip(model, brand);

  const version = reading.firmware?.trim().replace(/^(?:Ver\.|v)\s*/i, "");
  const parts = /^\d+(?:\.\d+){1,3}$/.test(version ?? "") ? version.split(".").map(Number) : [];
  const hasFirmware = parts[0] > 0;
  let mobile = null;
  for (const rule of mobileRules) {
    if (!rule.brands.includes(brand) || !rule.models.includes(model)) continue;
    if (rule.sonyMajor) {
      mobile = apps[!live(reading) || !hasFirmware ? "SonyFirmware" : parts[0] >= rule.sonyMajor ? "Creators" : "ImagingEdge"];
      break;
    }
    const minimum = rule.min;
    if (!minimum || (hasFirmware && (parts[0] > minimum[0] || (parts[0] === minimum[0] && parts[1] >= minimum[1])))) {
      mobile = apps[rule.app];
      break;
    }
    if (rule.fallback) {
      mobile = apps[rule.fallback];
      break;
    }
  }

  let desktopModel = model;
  if (brand === "CANON") {
    if (model === "EOS6DW" || model === "EOS6DWG") desktopModel = "EOS6D";
    if (model === "EOS70DW") desktopModel = "EOS70D";
  } else if (brand === "PANASONIC") {
    const name = strip(strip(model, "DC"), "DMC");
    desktopModel = lumixNames[name] ?? name;
  } else if (brand === "SONY") desktopModel = sonyAliases[model] ?? model;
  const desktop = (desktopRules[brand] ?? []).filter(rule => rule.models.includes(desktopModel));
  return { mobile, desktop };
}

export function lowerBound(count) {
  return count.isUpperBound && count.value > 0 ? Math.max(1, count.value - 999) : count.value;
}

const thresholds = ratio => ratio < 0.5 ? "Low" : ratio < 0.8 ? "Elevated" : "High";

export function usage(count, max) {
  if (count?.kind !== "Mechanical" || !max) return null;
  const ratio = count.value / max;
  const zone = thresholds(ratio);
  return thresholds(lowerBound(count) / max) === zone ? { ratio, zone } : null;
}

export function parseManualCount(text) {
  const digits = Array.from(text.trim(), digit => {
    if (!/\p{Nd}/u.test(digit)) return digit;
    const code = digit.codePointAt(0);
    let base = code;
    while (/\p{Nd}/u.test(String.fromCodePoint(base - 1))) base--;
    return (code - base) % 10;
  }).join("");
  const count = Number(digits);
  return /^\d+$/.test(digits) && Number.isSafeInteger(count) && count <= 0xFFFFFFFF ? count : null;
}

export function validAddress(input) {
  const parts = input.trim().split(".");
  return parts.length === 4 && parts.every(part => /^(0|[1-9]\d{0,2})$/.test(part) && Number(part) <= 255)
    && Number(parts[0]) > 0 && Number(parts[0]) < 224;
}

export function cameraPhotoFormat(reading) {
  const maker = reading.maker.toLowerCase();
  if (maker.startsWith("nikon") && /^nikon (?:d\d+|z(?:\s|r$))/i.test(reading.model)) return "NEF / JPEG";
  if (maker.startsWith("fujifilm")) return "RAF";
  if (maker.startsWith("sony")) return "ARW / JPEG";
  if (maker.startsWith("pentax") || /^pentax /i.test(reading.model)) return "PEF / DNG";
  return null;
}
