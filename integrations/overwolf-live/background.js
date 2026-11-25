/* Runs in Overwolf and sends its current game-event snapshot only to this PC. */
(() => {
  "use strict";
  const features = ["me", "kill", "death", "game_info", "match_info"];
  let info = {};
  let sending = false;
  let activeMatch = "";

  function merge(update) {
    if (!update || typeof update !== "object") return;
    for (const [category, fields] of Object.entries(update)) {
      if (!fields || typeof fields !== "object" || Array.isArray(fields)) continue;
      const id = category === "match_info" ? (fields.match_id || fields.matchId) : null;
      if (id && activeMatch && id !== activeMatch) info = {};
      if (id) activeMatch = String(id);
      info[category] = { ...(info[category] || {}), ...fields };
    }
  }

  // An update that arrives while a send is in flight is sent right after it, so a kill is not held back
  // until the next 2 s heartbeat.
  let pending = false;
  function done() {
    sending = false;
    if (pending) { pending = false; send(); }
  }

  function send() {
    if (!overwolf.io.paths.localAppData) return;
    if (sending) { pending = true; return; }
    sending = true;
    const path = overwolf.io.paths.localAppData + "\\ValoAutoPick\\spikeside-overwolf-bridge.json";
    overwolf.io.readTextFile(path, { encoding: overwolf.io.enums.eEncoding.UTF8 }, result => {
      if (!result.success) { done(); return; }
      let config;
      try { config = JSON.parse(result.content); } catch { done(); return; }
      if (!Number.isInteger(config.port) || config.port < 1 || config.port > 65535 || !/^[0-9A-F]{48}$/.test(config.token || "")) { done(); return; }
      fetch(`http://127.0.0.1:${config.port}/events?token=${config.token}`, {
        method: "POST", headers: { "Content-Type": "text/plain" }, body: JSON.stringify({ info })
      }).catch(() => {}).finally(done);
    });
  }

  overwolf.games.events.onInfoUpdates2.addListener(event => { merge(event.info); send(); });
  overwolf.games.events.onNewEvents.addListener(event => {
    if ((event.events || []).some(item => item.name === "match_end")) { info = {}; activeMatch = ""; }
    send();
  });
  function subscribe() {
    overwolf.games.events.setRequiredFeatures(features, result => {
      if (!result.success) { setTimeout(subscribe, 10000); return; }
      overwolf.games.events.getInfo(snapshot => { if (snapshot.success) merge(snapshot.res); send(); });
    });
  }
  subscribe();
  setInterval(send, 2000);
})();
