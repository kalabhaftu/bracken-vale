export function parseLyricsText(value) {
  const raw = String(value || "").replace(/^\uFEFF/, "").replace(/\r\n?/g, "\n");
  const lines = [];
  const timestamp = /\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,7}))?\]/g;
  let offsetMilliseconds = 0;
  for (const sourceLine of raw.split("\n")) {
    const offset = sourceLine.trim().match(/^\[offset:([+-]?\d+)\]$/i);
    if (offset) {
      const parsedOffset = Number(offset[1]);
      if (Number.isFinite(parsedOffset)) offsetMilliseconds = parsedOffset;
      continue;
    }
    const matches = [...sourceLine.matchAll(timestamp)];
    if (!matches.length) continue;
    for (let start = 0; start < matches.length;) {
      let end = start;
      while (end + 1 < matches.length) {
        const between = sourceLine.slice(matches[end].index + matches[end][0].length, matches[end + 1].index);
        if (between.trim()) break;
        end++;
      }
      const textStart = matches[end].index + matches[end][0].length;
      const textEnd = end + 1 < matches.length ? matches[end + 1].index : sourceLine.length;
      const text = sourceLine.slice(textStart, textEnd).trim();
      if (text) {
        for (let index = start; index <= end; index++) {
          const match = matches[index];
          const fraction = match[3] || "";
          const seconds = Number(match[1]) * 60 + Number(match[2]) + (fraction ? Number("0." + fraction) : 0) + offsetMilliseconds / 1000;
          if (Number.isFinite(seconds) && seconds >= 0) lines.push({ seconds, text });
        }
      }
      start = end + 1;
    }
  }
  lines.sort((a, b) => a.seconds - b.seconds);
  const plainText = lines.length ? "" : raw.trim();
  return { lines, plainText };
}

export function lyricLinesMarkup(lines, escapeHtml, seekEnabled = true) {
  return (lines || []).map((line, index) => {
    const seconds = Number(line.seconds);
    const text = escapeHtml(line.text || "");
    if (seekEnabled && Number.isFinite(seconds) && seconds >= 0)
      return `<button type="button" class="lyric-line" data-lyric-index="${index}" data-lyric-seconds="${seconds}" aria-label="Seek to ${Math.floor(seconds / 60)}:${String(Math.floor(seconds % 60)).padStart(2, "0")}">${text}</button>`;
    return `<p class="lyric-line" data-lyric-index="${index}">${text}</p>`;
  }).join("");
}
