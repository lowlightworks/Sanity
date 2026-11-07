// Sanity's anonymous stats endpoint: a Google Apps Script web app attached to a Google Sheet.
// The mod sends one small JSON packet per player per in-game day (while sharing is on). This script checks that
// it looks like a Sanity packet and appends it as one row of the "packets" sheet. It does nothing else: it reads no
// other data and keeps nothing about the sender (Apps Script doesn't pass on who sent a request).
// Setup steps are in tools/README.md.

const SHEET_NAME = 'packets';
const MAX_CHARS = 32 * 1024;   // a packet is 2-4 KB

function doPost(e) {
  try {
    const body = e && e.postData ? String(e.postData.contents || '') : '';
    if (body.length === 0 || body.length > MAX_CHARS) {
      return reply('rejected');
    }
    const packet = JSON.parse(body);
    if (!looksLikeAPacket(packet)) {
      return reply('rejected');
    }

    const lock = LockService.getScriptLock();
    lock.waitLock(10000);
    try {
      sheet().appendRow([
        new Date(),
        packet.schema,
        String(packet.mod).slice(0, 40),
        String(packet.session).slice(0, 16),
        Number(packet.day) || 0,
        packet.moon && typeof packet.moon.name === 'string' ? packet.moon.name.slice(0, 48) : '',
        Number(packet.players) || 0,
        body,
      ]);
    } finally {
      lock.releaseLock();
    }
    return reply('ok');
  } catch (err) {
    return reply('error');
  }
}

// Only the shape is checked: a schema number, a mod version, a session code and a day number.
function looksLikeAPacket(p) {
  return p !== null && typeof p === 'object'
    && typeof p.schema === 'number'
    && typeof p.mod === 'string'
    && typeof p.session === 'string' && /^[0-9a-f]{1,16}$/.test(p.session)
    && typeof p.day === 'number';
}

function sheet() {
  const book = SpreadsheetApp.getActiveSpreadsheet();
  let target = book.getSheetByName(SHEET_NAME);
  if (!target) {
    target = book.insertSheet(SHEET_NAME);
    target.appendRow(['received', 'schema', 'mod', 'session', 'day', 'moon', 'players', 'json']);
  }
  return target;
}

function reply(text) {
  return ContentService.createTextOutput(text);
}
