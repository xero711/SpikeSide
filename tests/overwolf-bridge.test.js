const assert = require("node:assert/strict");
const fs = require("node:fs");
const vm = require("node:vm");

const source = fs.readFileSync("integrations/overwolf-live/background.js", "utf8");
const sent = [];
const listeners = {};
const token = "A".repeat(48);
const overwolf = {
  io: {
    paths: { localAppData: "C:\\Local" }, enums: { eEncoding: { UTF8: "utf8" } },
    readTextFile: (_path, _options, callback) => callback({ success: true, content: JSON.stringify({ port: 32123, token }) })
  },
  games: { events: {
    onInfoUpdates2: { addListener: fn => { listeners.info = fn; } },
    onNewEvents: { addListener: fn => { listeners.events = fn; } },
    setRequiredFeatures: (_features, callback) => callback({ success: true }),
    getInfo: callback => callback({ success: true, res: { match_info: { match_id: "match-a" } } })
  } }
};
const context = { overwolf, setTimeout() {}, setInterval() {}, fetch: (url, options) => {
  sent.push({ url, body: JSON.parse(options.body) });
  return Promise.resolve({ ok: true });
} };

(async () => {
  vm.runInNewContext(source, context);
  await new Promise(resolve => setImmediate(resolve));
  listeners.info({ info: { match_info: { scoreboard_0: JSON.stringify({ player_id: "self", kills: 2, deaths: 1, assists: 3 }) } } });
  await new Promise(resolve => setImmediate(resolve));
  assert.match(sent.at(-1).url, /^http:\/\/127\.0\.0\.1:32123\/events\?token=A{48}$/);
  assert.equal(sent.at(-1).body.info.match_info.match_id, "match-a");
  assert.equal(JSON.parse(sent.at(-1).body.info.match_info.scoreboard_0).kills, 2);
  listeners.info({ info: { match_info: { match_id: "match-b" } } });
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(sent.at(-1).body.info.match_info.match_id, "match-b");
  assert.equal(sent.at(-1).body.info.match_info.scoreboard_0, undefined);
  console.log("Overwolf companion snapshots and match reset: PASS");

  // Updates that arrive while a send is in flight go out right after it, carrying the newest values.
  const held = [];
  context.fetch = (url, options) => { sent.push({ url, body: JSON.parse(options.body) }); return new Promise(resolve => held.push(resolve)); };
  const before = sent.length;
  listeners.info({ info: { match_info: { scoreboard_0: JSON.stringify({ player_id: "self", kills: 3, deaths: 1, assists: 3 }) } } });
  await new Promise(resolve => setImmediate(resolve));
  listeners.info({ info: { match_info: { scoreboard_0: JSON.stringify({ player_id: "self", kills: 4, deaths: 1, assists: 3 }) } } });
  listeners.info({ info: { match_info: { scoreboard_0: JSON.stringify({ player_id: "self", kills: 5, deaths: 1, assists: 3 }) } } });
  assert.equal(sent.length, before + 1);
  held.shift()({ ok: true });
  await new Promise(resolve => setImmediate(resolve));
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(sent.length, before + 2);
  assert.equal(JSON.parse(sent.at(-1).body.info.match_info.scoreboard_0).kills, 5);
  console.log("Overwolf companion resends updates held during a send: PASS");
})().catch(error => { console.error(error); process.exitCode = 1; });
