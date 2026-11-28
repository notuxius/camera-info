
import assert from "node:assert/strict";
import test from "node:test";
import { companions, lowerBound, parseManualCount, rating, usage, validAddress } from "../ui/camera.js";

test("manual entries accept localized whole numbers up to the uint32 limit", () => {
  assert.equal(parseManualCount("৮৭০০০"), 87000);
  assert.equal(parseManualCount(" ٤٢ "), 42);
  assert.equal(parseManualCount("4294967295"), 4294967295);
  for (const bad of ["", "-1", "4,000", "1.5", "4294967296"]) assert.equal(parseManualCount(bad), null);
});

test("usage preserves uncertain counts and threshold crossings", () => {
  const bound = { value: 75000, isUpperBound: true, kind: "Mechanical" };
  assert.equal(lowerBound(bound), 74001);
  assert.equal(usage(bound, 150000), null);
  assert.equal(usage({ ...bound, isUpperBound: false }, 150000).zone, "Elevated");
  assert.equal(usage({ ...bound, value: 87000 }, 150000).zone, "Elevated");
  assert.equal(usage({ ...bound, kind: "Image" }, 150000), null);
  assert.equal(rating({ model: "Canon EOS 5D Mark III" })[0], 150000);
  assert.equal(rating({ model: "Nikon Z 6III" })[0], 200000);
});

test("keeps firmware-sensitive and platform-specific companion apps", () => {
  const canon = companions({ maker: "Canon", model: "Canon EOS 5D Mark III", source: "Usb" });
  assert.equal(canon.desktop.find(app => app.name === "ShutterCheck").windows, null);
  assert.ok(canon.desktop.find(app => app.name === "ShutterCheck").macOS.startsWith("https://"));
  assert.equal(companions({ maker: "Fujifilm", model: "Fujifilm X-E5" }).mobile.mechanical, true);
  assert.ok(companions({ maker: "Sony", model: "Sony ILCE-7M4", firmware: "4.0", source: "Arw" }).mobile.compatibility);
  assert.equal(companions({ maker: "Sony", model: "Sony ILCE-7M4", firmware: "Ver. 4.0", source: "Usb" }).mobile.name, "Creators’ App");
});

test("direct Wi-Fi entry accepts IPv4 addresses without ports", () => {
  assert.ok(validAddress("192.168.1.42"));
  for (const bad of ["192.168.1.42:15740", "01.1.2.3", "239.255.255.250", "127.0.1"]) assert.equal(validAddress(bad), false);
});
