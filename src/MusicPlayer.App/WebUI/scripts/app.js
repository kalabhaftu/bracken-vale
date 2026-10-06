import { command, onEvent } from "./api.js";
import { createLibraryViews } from "./library.js";
import { createPlayerUi } from "./player.js";

const $ = (selector, root = document) => root.querySelector(selector);
const $$ = (selector, root = document) => [...root.querySelectorAll(selector)];
const icons = {
  home:"M3 10.8 12 3l9 7.8v9.7a.5.5 0 0 1-.5.5h-6v-6h-5v6h-6a.5.5 0 0 1-.5-.5z",
  search:"M20 20l-4.5-4.5M18 10.5a7.5 7.5 0 1 1-15 0 7.5 7.5 0 0 1 15 0Z",
  clock:"M12 7v5l3 2m6-2a9 9 0 1 1-18 0 9 9 0 0 1 18 0Z",
  heart:"M20.8 8.7c0 4.1-8.8 10-8.8 10s-8.8-5.9-8.8-10A4.7 4.7 0 0 1 12 6.4a4.7 4.7 0 0 1 8.8 2.3Z",
  music:"M9 18V5l12-2v13M9 18c0 1.4-1.4 2.5-3 2.5S3 19.4 3 18s1.4-2.5 3-2.5 3 1.1 3 2.5Zm12-2c0 1.4-1.4 2.5-3 2.5s-3-1.1-3-2.5 1.4-2.5 3-2.5 3 1.1 3 2.5Z",
  disc:"M12 3a9 9 0 1 0 0 18 9 9 0 0 0 0-18Zm0 6a3 3 0 1 0 0 6 3 3 0 0 0 0-6Zm0 3h.01",
  users:"M16 21v-2a4 4 0 0 0-4-4H8a4 4 0 0 0-4 4v2m16 0v-2a4 4 0 0 0-3-3.87M14 3.13a4 4 0 0 1 0 7.75M10 7a4 4 0 1 1-8 0 4 4 0 0 1 8 0Z",
  tag:"M20.6 13.6 13.7 20.5a2 2 0 0 1-2.8 0L3.5 13.1V3.5h9.6l7.5 7.4a2 2 0 0 1 0 2.7ZM7.5 7.5h.01",
  trending:"M3 17l6-6 4 4 8-8M15 7h6v6",
  spark:"m12 3 1.7 5.3L19 10l-5.3 1.7L12 17l-1.7-5.3L5 10l5.3-1.7L12 3Zm7 12 .8 2.2L22 18l-2.2.8L19 21l-.8-2.2L16 18l2.2-.8L19 15Z",
  list:"M8 6h13M8 12h13M8 18h13M3 6h.01M3 12h.01M3 18h.01",
  folder:"M3 6.5A1.5 1.5 0 0 1 4.5 5H10l2 2h7.5A1.5 1.5 0 0 1 21 8.5v9a1.5 1.5 0 0 1-1.5 1.5h-15A1.5 1.5 0 0 1 3 17.5z",
  headphones:"M3 13v-1a9 9 0 0 1 18 0v1m-18 0v5a2 2 0 0 0 2 2h2v-8H5a2 2 0 0 0-2 1Zm18 0v5a2 2 0 0 1-2 2h-2v-8h2a2 2 0 0 1 2 1Z",
  lyrics:"M4 5h16M4 10h16M4 15h10M4 20h12",
  queue:"M4 6h12M4 11h12M4 16h7m4-2v7l6-3.5z",
  sliders:"M4 21v-7m0-4V3m8 18v-9m0-4V3m8 18v-5m0-4V3M2 14h4m4-6h4m4 6h4",
  copy:"M8 8V4h12v12h-4M4 8h12v12H4z",
  settings:"M12 8a4 4 0 1 0 0 8 4 4 0 0 0 0-8Zm0-5v2m0 14v2m9-9h-2M5 12H3m15.4-6.4-1.4 1.4M7 17l-1.4 1.4m12.8 0L17 17M7 7 5.6 5.6",
  "chevron-left":"m15 18-6-6 6-6", "chevron-right":"m9 18 6-6-6-6", close:"M18 6 6 18M6 6l12 12",
  refresh:"M20 7v5h-5M4 17v-5h5m-4-3a8 8 0 0 1 13.6-3L20 7M4 17l1.4 1.4A8 8 0 0 0 19 15",
  shuffle:"m18 14 4 4-4 4m0-20 4 4-4 4M2 18h2.5a5 5 0 0 0 4-2l7-8a5 5 0 0 1 4-2H22M2 6h2.5a5 5 0 0 1 4 2l1.5 1.7m4 4.6 1.5 1.7a5 5 0 0 0 4 2H22",
  "skip-back":"M19 20 9 12l10-8v16ZM5 19V5", "skip-forward":"m5 4 10 8-10 8V4Zm14 1v14",
  play:"m7 4 14 8-14 8V4Z", pause:"M7 5h3v14H7zm7 0h3v14h-3z", repeat:"m17 2 4 4-4 4M3 11V9a3 3 0 0 1 3-3h15M7 22l-4-4 4-4m14-1v2a3 3 0 0 1-3 3H3",
  speaker:"M3 9v6h4l5 4V5L7 9H3Zm13-2a7 7 0 0 1 0 10m2-13a11 11 0 0 1 0 16", volume:"M3 9v6h4l5 4V5L7 9H3Zm13-2a7 7 0 0 1 0 10", mute:"M3 9v6h4l5 4V5L7 9H3Zm13 1 5 5m0-5-5 5"
};

function svg(name) { return `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="${icons[name] || icons.music}"/></svg>`; }
function paintIcons(root = document) { $$('[data-icon]', root).forEach(node => { const name = node.dataset.icon; node.innerHTML = svg(name); }); }
function esc(value) { return String(value ?? "").replace(/[&<>"']/g, c => ({"&":"&amp;","<":"&lt;",">":"&gt;",'"':"&quot;","'":"&#39;"}[c])); }
function initials(value) { return String(value || "MP").split(/\s+/).filter(Boolean).slice(0,2).map(word => word[0]).join("").toUpperCase() || "MP"; }
function cover(item, extra = "") {
  const seed = String(item?.album ? `${item.albumArtist||""}:${item.album}` : item?.artist || item?.name || item?.title || item?.id || "music");
  let hash = 2166136261; for (const ch of seed) hash = Math.imul(hash ^ ch.charCodeAt(0),16777619);
  const cls = `c${Math.abs(hash >>> 0) % 10 + 1}`;
  const artwork = item?.artworkUrl ? `<img loading="lazy" src="${esc(item.artworkUrl)}" alt="" onerror="this.onerror=null;this.hidden=true;if(this.nextElementSibling)this.nextElementSibling.hidden=false">` : "";
  const mark=initials(item?.album || item?.artist || item?.name || item?.title);
  return `<div class="cover ${cls} ${extra}">${artwork}<div class="cover-art" ${artwork?"hidden":""}>${esc(mark)}</div></div>`;
}
function titleOf(item) { return item?.name || item?.title || "Unknown"; }
function subOf(item) { return item?.artist || item?.albumArtist || item?.meta || ""; }
function fmtDuration(seconds) { const n = Math.max(0, Math.floor(Number(seconds) || 0)); return n >= 3600 ? `${Math.floor(n/3600)}:${String(Math.floor(n/60)%60).padStart(2,"0")}:${String(n%60).padStart(2,"0")}` : `${Math.floor(n/60)}:${String(n%60).padStart(2,"0")}`; }
function bytesLabel(bytes) { const value = Number(bytes)||0; return value > 1024**3 ? `${(value/1024**3).toFixed(1)} GB` : value > 1024**2 ? `${(value/1024**2).toFixed(0)} MB` : `${(value/1024).toFixed(0)} KB`; }

const state = { view:"Home", search:"", filter:"all", searchQuery:"", searchPages:{songs:0,albums:0,artists:0,playlists:0}, searchResults:{}, duplicateSort:"Title", duplicateDescending:false, duplicateOffset:0, sort:"Title", descending:false, offset:0, pageSize:200, total:0, items:[], group:null, playlist:null, track:null, trackDetails:null, playing:false, position:0, duration:0, volume:75, shuffle:false, repeat:"Off", repeatA:null, repeatB:null, queue:[], queueTotal:0, queueOffset:0, queueIndex:-1, panel:"queue", panelOpen:true, history:["Home"], historyIndex:0, settings:{}, modal:null, muted:false, scan:null, loading:false, lyricLines:[] };
const playerUi = createPlayerUi({state,$,$$,call,command,cover,esc,fmtDuration,svg}); const {updatePlayer,updateActiveLyric,updatePanel,loadTrackDetails,refreshCurrent,setVolume,toggleMute}=playerUi;
let toastTimer;

async function call(name,payload={}) { try { return await command(name,payload); } catch (error) { toast(error.message || "Something went wrong."); throw error; } }
function toast(message) { const box=$("#toast"); box.textContent=message; box.classList.add("show"); clearTimeout(toastTimer); toastTimer=setTimeout(()=>box.classList.remove("show"),2800); }
function setTheme(settings) {
  const theme=String(settings?.theme||"System").toLowerCase();
  const resolvedTheme=theme==="light"||theme==="system"&&settings?.resolvedTheme==="Light"?"light":"dark";
  document.documentElement.dataset.theme=resolvedTheme;
  document.documentElement.dataset.motion=String(settings?.motionStyle||"Subtle").toLowerCase();
  const iconVariant=resolvedTheme==="light"?"light":settings?.accentMode==="Artwork"?"accent":"dark";
  const brandLogo=$("#brandLogo");
  if(brandLogo) brandLogo.src=`https://assets.musicplayer.local/MusicPlayer-${iconVariant}.png`;
  const color=settings?.accentManual&&settings?.accentColor?settings.accentColor:settings?.accentMode==="Artwork"&&settings?.artworkAccent?settings.artworkAccent:"#b7ff2d";
  if(/^#[0-9a-f]{6}$/i.test(color)){
    document.documentElement.style.setProperty("--accent",color);
    const r=parseInt(color.slice(1,3),16),g=parseInt(color.slice(3,5),16),b=parseInt(color.slice(5,7),16);
    document.documentElement.style.setProperty("--accent-rgb",`${r},${g},${b}`);
  }
  if(settings?.navigationWidth) document.documentElement.style.setProperty("--sidebar",`${Number(settings.navigationWidth)}px`);
  if(settings?.browseWidth) document.documentElement.style.setProperty("--browse-width",`${Number(settings.browseWidth)}px`);
  applyNavigationSettings(settings);
}

function applyNavigationSettings(settings=state.settings) {
  const items=$$("#navigation .nav-item[data-view]"); const stored=Array.isArray(settings?.navigationOrder)?settings.navigationOrder:[];
  const order=[...stored,...items.map(item=>item.dataset.view).filter(view=>!stored.includes(view))];
  const hidden=new Set(Array.isArray(settings?.hiddenPanels)?settings.hiddenPanels:[]);
  for(const item of items){ item.hidden=hidden.has(item.dataset.view); item.style.order=String(Math.max(0,order.indexOf(item.dataset.view))); }
  const sections=$$("#navigation .nav-section"); sections.forEach(section=>{ const heading=section.querySelector(".nav-title"); if(heading){ const rank=Math.min(...$$(".nav-item[data-view]",section).map(item=>Number(item.style.order)||0)); heading.style.order=String(Number.isFinite(rank)?rank-.25:0); } });
}

const libraryViews = createLibraryViews({state,$,$$,svg,paintIcons,esc,initials,cover,titleOf,subOf,fmtDuration,bytesLabel,call,toast,setTheme,applyNavigationSettings,openModal,updatePlayer,updatePanel});
const {trackRow,songTable,card,cardGrid,section,viewHeader,toolbar,navigate,renderView,renderHome,renderSearch,viewFilter,renderTracksView,renderGroupsView,renderFolders,renderPlaylists,renderPlaylistDetail,renderGroupDetail,renderQueue,renderNowPlaying,renderLyrics,renderAudio,renderSettings,navigationSettingsMarkup,renderDuplicates,openDuplicateFiles}=libraryViews;
function openModal(title,body,actions) { state.modal={title,body,actions}; $("#modalTitle").textContent=title; $("#modalBody").innerHTML=body; $("#modalActions").innerHTML=actions||`<button class="action" data-modal-close>Close</button>`; $("#modalLayer").hidden=false; paintIcons($("#modalLayer")); $("#modalBody input,#modalBody textarea")[0]?.focus(); }
function closeModal() { $("#modalLayer").hidden=true; state.modal=null; }
function askConfirm(title,message,confirmLabel,action) { openModal(title,`<p>${esc(message)}</p>`,`<button class="action" data-modal-close>Cancel</button><button class="action ${confirmLabel.toLowerCase().includes("delete")?"danger":"primary"}" data-confirm="${esc(action)}">${esc(confirmLabel)}</button>`); }
function contextMenu(x,y,id) { const menu=$("#contextMenu"); menu.dataset.id=id; menu.innerHTML=`<button data-menu-action="play">Play</button><button data-menu-action="play-next">Play next</button><button data-menu-action="queue">Add to queue</button><button data-menu-action="playlist">Add to playlist</button><button data-menu-action="create-playlist-with-track">Create playlist with this song…</button><button data-menu-action="favorite">Toggle favorite</button><button data-menu-action="rating">Set rating…</button><hr><button data-menu-action="lyrics">Lyrics</button><button data-menu-action="tags">Edit tags</button><button data-menu-action="restore-tags">Restore previous tags</button><button data-menu-action="details">Track details</button><button data-menu-action="location">Show in File Explorer</button>`; menu.style.left=`${Math.min(x,innerWidth-230)}px`; menu.style.top=`${Math.min(y,innerHeight-370)}px`; menu.classList.add("show"); }

async function addToPlaylist(trackId) { const data=await call("getPlaylists"); if(!data.playlists?.length) return newPlaylist(trackId); openModal("Add to playlist",`<label class="modal-field"><span>Choose a playlist</span><select class="field" id="playlistChoice">${data.playlists.map(p=>`<option value="${esc(p.id)}">${esc(p.name)}</option>`).join("")}</select></label>`,`<button class="action" data-modal-close>Cancel</button><button class="action primary" data-modal-command="add-to-playlist" data-track-id="${esc(trackId)}">Add</button>`); }
function newPlaylist(trackId="") { openModal("New playlist",`<label class="modal-field"><span>Playlist name</span><input class="field" id="playlistName" maxlength="120" placeholder="Playlist name"></label>`,`<button class="action" data-modal-close>Cancel</button><button class="action primary" data-modal-command="create-playlist" data-track-id="${esc(trackId)}">Create playlist</button>`); }
async function openDetails(id) {
  const [d,tags]=await Promise.all([call("getTrackDetails",{id}),call("getTags",{id})]);const t=d.track;
  const fields=[["Title",t.title],["Artist",t.artist],["Album",t.album],["Album artist",t.albumArtist],["Genre",t.genre],["Year",t.year],["Track number",t.trackNumber],["Format",d.container],["Duration",fmtDuration(d.durationSeconds)],["Bitrate",d.bitrateKbps?`${d.bitrateKbps} kbps`:"—"],["Sample rate",d.sampleRateHz?`${d.sampleRateHz} Hz`:"—"],["Bits per sample",d.bitsPerSample||"—"],["File size",bytesLabel(d.fileSize)],["Modified",d.modifiedDisplay],["Location",d.path]];
  const additional=Object.entries(tags.additionalFields||{}).filter(([,value])=>String(value||"").trim()).map(([key,value])=>[key.replaceAll("_"," "),value]);
  const custom=Object.entries(tags.customFields||{}).map(([key,value])=>[key,value]);
  const backups=(tags.backups||[]).map(item=>item.created);
  const detailList=items=>items.length?`<dl class="details">${items.map(([key,value])=>`<div><dt>${esc(key)}</dt><dd>${esc(value||"—")}</dd></div>`).join("")}</dl>`:`<p class="muted">None recorded.</p>`;
  openModal("Track details",`${detailList(fields)}<h3>Additional standard tags</h3>${detailList(additional)}<h3>Custom tags</h3>${detailList(custom)}<h3>Recent tag backups</h3>${backups.length?`<ul>${backups.map(created=>`<li>${esc(created)}</li>`).join("")}</ul>`:`<p class="muted">No tag backups.</p>`}`);
}

async function editLyrics(initialText = null) { const d=await call("getLyrics",{id:state.track?.id||state.trackId}); openModal("Edit lyrics",`<p>${esc(d.track?.title||"")} · ${esc(d.track?.artist||"")}</p><div class="toolbar"><button class="action" id="stampLyric">Timestamp at current position</button><label>Timing offset (ms) <input class="field" id="lyricsOffset" type="number" value="${Number(d.offsetMilliseconds)||0}"></label><select class="select" id="lyricsMode"><option value="sidecar">Save beside audio file</option><option value="embed">Embed in audio file</option></select></div><textarea class="textarea" id="lyricsEditor">${esc(initialText??d.raw??"")}</textarea>`,`<button class="action" data-modal-close>Cancel</button><button class="action primary" data-modal-command="save-lyrics" data-track-id="${esc(d.track?.id||"")}">Save lyrics</button>`); $("#stampLyric").addEventListener("click",()=>{const editor=$("#lyricsEditor"),time=Math.max(0,state.position),m=Math.floor(time/60),s=(time%60).toFixed(2).padStart(5,"0"),stamp=`[${m}:${s}]`;const p=editor.selectionStart;editor.setRangeText(`${stamp}`,p,p,"end");editor.focus();}); }
async function editTags(id) { const d=await call("getTags",{id}); const t=d.track; const fields=[["title","Title"],["artist","Artist"],["album","Album"],["albumArtist","Album artist"],["genre","Genre"],["year","Year"],["trackNumber","Track number"]]; const custom=Object.entries(d.customFields||{}).map(([key,value])=>`<label class="modal-field"><span>${esc(key)}</span><input class="field" data-custom-tag="${esc(key)}" value="${esc(value)}"></label>`).join("");const additional=Object.entries(d.additionalFields||{}).map(([key,value])=>`<label class="modal-field"><span>${esc(key.replaceAll("_"," "))}</span><input class="field" data-additional-tag="${esc(key)}" value="${esc(value)}"></label>`).join(""); openModal("Edit tags",`${fields.map(([key,label])=>`<label class="modal-field"><span>${label}</span><input class="field" data-tag="${key}" value="${esc(t[key]||"")}"></label>`).join("")}<div class="toolbar"><button class="action" id="chooseArtwork">Choose artwork…</button><span id="artworkPath"></span></div>${custom?`<details><summary>Additional format-specific fields</summary>${custom}</details>`:""}${additional?`<details><summary>Additional standard fields</summary>${additional}</details>`:""}<p>${esc(d.customFormat||"Audio tags")} · Saving creates a recoverable backup.</p>`,`<button class="action" data-modal-close>Cancel</button><button class="action primary" data-modal-command="save-tags" data-track-id="${esc(id)}">Save tags</button>`); $("#modalLayer").dataset.artworkToken="";$("#chooseArtwork").addEventListener("click",async()=>{const picked=await call("pickArtwork");if(picked?.token){$("#modalLayer").dataset.artworkToken=picked.token;$("#artworkPath").textContent=picked.name;}}); }

async function action(name,el) {
  const id=el?.dataset.id||el?.dataset.trackId||""; const view=state.view;
  const trackContext={view,search:state.search,sort:state.sort,descending:state.descending,group:state.group,playlistId:state.playlist?.id};
  switch(name) {
    case "play": case "play-track": await call("playTrack",{id,...trackContext}); await refreshCurrent(); break;
    case "play-view": await call("playView",{view:state.view,group:state.group}); await refreshCurrent(); break;
    case "shuffle-view": await call("shuffleView",{view:state.view,group:state.group}); await refreshCurrent(); break;
    case "play-group": await call("playGroup",{type:el.dataset.type,id}); await refreshCurrent(); break;
    case "favorite": await call("toggleFavorite",{id:id||state.track?.id}); await refreshCurrent(); await renderView(); break;
    case "context": contextMenu(el.getBoundingClientRect().right,el.getBoundingClientRect().bottom,id); break;
    case "play-next": await call("playNext",{id}); await refreshCurrent(); break;
    case "queue": await call("addToQueue",{id}); await refreshCurrent(); break;
    case "playlist": await addToPlaylist(id); break;
    case "rating": openModal("Set rating",`<label class="modal-field"><span>Rating</span><select class="field" id="ratingValue"><option value="0">Unrated</option>${[1,2,3,4,5].map(n=>`<option value="${n}">${"★".repeat(n)}</option>`).join("")}</select></label>`,`<button class="action" data-modal-close>Cancel</button><button class="action primary" data-modal-command="set-rating" data-track-id="${esc(id)}">Save</button>`); break;
    case "tags": await editTags(id); break;
    case "restore-tags": {const d=await call("getTags",{id});if(!d.backups?.length){toast("No saved tag backups are available.");break;}const choice=d.backups.length>1?`<label class="modal-field"><span>Backup version</span><select class="field" id="backupChoice">${d.backups.map(b=>`<option value="${esc(b.id)}">${esc(b.created)}</option>`).join("")}</select></label>`:"";openModal("Restore previous tags?",`<p>The selected version replaces the current tags. The current file will also be backed up.</p>${choice}`,`<button class="action" data-modal-close>Cancel</button><button class="action danger" data-confirm="restore-tags">Restore tags</button>`);$("#modalLayer").dataset.trackId=id;if(d.backups.length===1)$("#modalLayer").dataset.backupId=d.backups[0].id;break;}
    case "lyrics": state.trackId=id; await navigate("Lyrics"); break;
    case "details": await openDetails(id||state.track?.id); break;
    case "location": case "show-location": await call("showInFolder",{id}); break;
    case "new-playlist": newPlaylist(); break;
    case "import-playlist": await call("importPlaylist"); await renderView(); break;
    case "create-playlist-with-track": newPlaylist(id); break;
    case "rename-playlist": openModal("Rename playlist",`<label class="modal-field"><span>Playlist name</span><input class="field" id="playlistName" maxlength="120" value="${esc(state.playlist.name)}"></label>`,`<button class="action" data-modal-close>Cancel</button><button class="action primary" data-modal-command="rename-playlist">Save</button>`); break;
    case "delete-playlist": askConfirm("Delete playlist?",`Delete “${state.playlist.name}”? The music files will not be changed.`,"Delete playlist","delete-playlist"); break;
    case "play-playlist": await call("playPlaylist",{playlistId:state.playlist.id}); await refreshCurrent(); break;
    case "shuffle-playlist": await call("shufflePlaylist",{playlistId:state.playlist.id}); await refreshCurrent(); break;
    case "export-playlist": await call("exportPlaylist",{playlistId:state.playlist.id}); break;
    case "remove-playlist-track": await call("removePlaylistTrack",{playlistId:state.playlist.id,position:Number(el.dataset.position)}); await renderView(); break;
    case "add-folder": await call("addFolder"); await renderView(); break;
    case "add-exclusion": await call("addExclusion"); await manageExclusions(); break;
    case "remove-exclusion": await call("removeExclusion",{path:el.dataset.path}); await manageExclusions(); break;
    case "show-file": await call("showFilePath",{path:el.dataset.path}); break;
    case "manage-roots": await manageRoots(); break;
    case "manage-exclusions": await manageExclusions(); break;
    case "remove-root": askConfirm("Remove folder?",`Stop scanning ${el.dataset.path} and remove its unshared tracks from the index? Music files and playlist entries remain.`,"Remove folder","remove-root"); $("#modalLayer").dataset.path=el.dataset.path; break;
    case "scan": await call("scanLibrary"); break;
    case "scan-pause": await call("toggleScanPause"); break;
    case "scan-cancel": await call("cancelScan"); break;
    case "clear-queue": askConfirm("Clear upcoming queue?","Remove upcoming tracks from the queue? The current track keeps playing.","Clear queue","clear-queue"); break;
    case "play-queue": await call("playQueueEntry",{index:Number(el.dataset.index)}); await refreshCurrent(); await renderView(); break;
    case "queue-up": await call("moveQueue",{index:Number(el.dataset.index),direction:-1}); await refreshCurrent(); await renderView(); break;
    case "queue-down": await call("moveQueue",{index:Number(el.dataset.index),direction:1}); await refreshCurrent(); await renderView(); break;
    case "queue-remove": await call("removeQueue",{index:Number(el.dataset.index)}); await refreshCurrent(); await renderView(); break;
    case "edit-lyrics": await editLyrics(); break;
    case "search-lyrics": { const d=await call("searchLyrics",{id:state.track?.id||state.trackId}); openModal("LRCLIB results",(d.results||[]).map((r,i)=>`<button class="action" data-lyric-result="${i}">${esc(r.trackName)} — ${esc(r.artistName)}${r.albumName?` · ${esc(r.albumName)}`:""}</button>`).join("<br>")||"No results found."); $("#modalLayer").dataset.lyrics=JSON.stringify(d.results||[]); break; }
    case "refresh-devices": await call("refreshAudioDevices"); await renderView(); break;
    case "save-eq": openModal("Save equalizer preset",`<label class="modal-field"><span>Preset name</span><input class="field" id="presetName" maxlength="60"></label>`,`<button class="action" data-modal-close>Cancel</button><button class="action primary" data-modal-command="save-eq">Save preset</button>`); break;
    case "check-updates": await call("checkUpdates"); break;
    case "open-release": await call("openRelease",{url:el.dataset.url}); break;
    case "open-default-apps": await call("openDefaultApps"); break;
    case "open-logs": await call("openLogs"); break;
    case "export-logs": await call("exportLogs"); break;
    case "toggle-mute": await toggleMute(); break;
    case "save-settings": await saveSettings(); break;
    case "load-more": el.closest(".load-more")?.remove(); if(state.view==="Queue"){state.offset+=200;await renderView(true);}else{state.offset+=state.pageSize;if(["Albums","Artists","Genres"].includes(state.view)) await renderGroupsView(true); else await renderView(true);} break;
    case "load-folders": el.closest(".load-more")?.remove(); await renderView(true); break;
    case "direction": state.descending=!state.descending; state.offset=0; await renderView(); break;
    case "search-page": {const kind=el.dataset.type;state.searchPages[kind]=Math.max(0,(state.searchPages[kind]||0)+Number(el.dataset.direction||0));await renderView();break;}
    case "duplicate-page": state.duplicateOffset=Math.max(0,state.duplicateOffset+Number(el.dataset.direction||0)*100); await renderView(); break;
    case "duplicate-direction": state.duplicateDescending=!state.duplicateDescending; state.duplicateOffset=0; await renderView(); break;
    case "duplicate-files": await openDuplicateFiles(id); break;
    case "duplicate-files-page": await openDuplicateFiles(id,Math.max(0,Number(el.dataset.offset)||0)); break;
    default: break;
  }
}

async function manageRoots() { const d=await call("getFolders",{rootsOnly:true}); openModal("Library folders",(d.roots||[]).map(r=>`<div class="setting-row"><div><b>${esc(r.name||r.path)}</b><span>${esc(r.path)} · ${Number(r.trackCount||0)} tracks</span></div><button class="action" data-action="remove-root" data-path="${esc(r.path)}">Remove</button></div>`).join("")+`<button class="action" data-action="add-folder">Add folder…</button>`); }
async function manageExclusions() { const d=await call("getExclusions"); openModal("Scan exclusions",`<p>Windows and common app/cache locations are skipped automatically. Excluding a folder also excludes its subfolders.</p>${(d.paths||[]).map(p=>`<div class="setting-row"><span>${esc(p)}</span><button class="action" data-action="remove-exclusion" data-path="${esc(p)}">Remove</button></div>`).join("")}<button class="action" data-action="add-exclusion">Add ignored folder…</button>`); }
async function saveSettings() { const settings={}; $$('[data-setting]').forEach(el=>settings[el.dataset.setting]=el.matches(".toggle")?el.classList.contains("on"):el.value);settings.navigationOrder=state.settings.navigationOrder||[];settings.hiddenPanels=state.settings.hiddenPanels||[];await call("updateSettings",{settings});state.settings={...state.settings,...settings};setTheme(state.settings);toast("Settings saved.");await renderView(); }

async function handleMenu(actionName,id) { $("#contextMenu").classList.remove("show"); const actions={play:"play-track","play-next":"play-next",queue:"queue",playlist:"playlist","create-playlist-with-track":"create-playlist-with-track",favorite:"favorite",rating:"rating",lyrics:"lyrics",tags:"tags","restore-tags":"restore-tags",details:"details",location:"location"}; await action(actions[actionName],{dataset:{id}}); }

document.addEventListener("click",async e=>{
  const nav=e.target.closest("[data-view]"); if(nav){ e.preventDefault(); await navigate(nav.dataset.view||"Home"); return; }
  const menu=e.target.closest("[data-menu-action]"); if(menu){ await handleMenu(menu.dataset.menuAction,$("#contextMenu").dataset.id); return; }
  const filter=e.target.closest("[data-filter]"); if(filter){ state.filter=filter.dataset.filter; await renderView(); return; }
  const open=e.target.closest("[data-open-type]"); if(open && !e.target.closest("[data-action]")){ const type=open.dataset.openType; if(type==="playlist"){state.playlist={id:open.dataset.openId,name:open.dataset.openName};await navigate("Playlist");} else {state.group={column:type,name:open.dataset.openName,id:open.dataset.openId};await navigate(type==="album"?"Album":type==="artist"?"Artist":type==="folder"?"Folder":"Genre");} return; }
  const btn=e.target.closest("[data-action]"); if(btn){ await action(btn.dataset.action,btn); return; }
  const track=e.target.closest("[data-track]"); if(track){ await action("play-track",{dataset:{id:track.dataset.track}}); return; }
  const modalCmd=e.target.closest("[data-modal-command]"); if(modalCmd){ const kind=modalCmd.dataset.modalCommand; try {
    if(kind==="create-playlist"){const result=await call("createPlaylist",{name:$("#playlistName").value,trackId:modalCmd.dataset.trackId});closeModal();if(result?.playlist){state.playlist=result.playlist;await navigate("Playlist");}else await renderView();}
    else if(kind==="rename-playlist"){await call("renamePlaylist",{playlistId:state.playlist.id,name:$("#playlistName").value});state.playlist.name=$("#playlistName").value;closeModal();await renderView();}
    else if(kind==="add-to-playlist"){await call("addToPlaylist",{playlistId:$("#playlistChoice").value,trackId:modalCmd.dataset.trackId});closeModal();toast("Added to playlist.");}
    else if(kind==="set-rating"){await call("setRating",{id:modalCmd.dataset.trackId,rating:Number($("#ratingValue").value)});closeModal();await renderView();}
    else if(kind==="save-lyrics"){await call("saveLyrics",{id:modalCmd.dataset.trackId,text:$("#lyricsEditor").value,mode:$("#lyricsMode").value,offsetMilliseconds:Number($("#lyricsOffset").value)||0});closeModal();toast("Lyrics saved.");await renderView();}
    else if(kind==="save-tags"){const tags={};$$('[data-tag]').forEach(el=>tags[el.dataset.tag]=el.value);tags.customFields={};$$('[data-custom-tag]').forEach(el=>tags.customFields[el.dataset.customTag]=el.value);tags.additionalFields={};$$('[data-additional-tag]').forEach(el=>tags.additionalFields[el.dataset.additionalTag]=el.value);await call("saveTags",{id:modalCmd.dataset.trackId,tags,artworkToken:$("#modalLayer").dataset.artworkToken||""});closeModal();toast("Tags saved with a backup.");await renderView();}
    else if(kind==="save-eq"){await call("saveEqualizerPreset",{name:$("#presetName").value});closeModal();await renderView();}
  } catch {} return; }
  const confirm=e.target.closest("[data-confirm]"); if(confirm){const kind=confirm.dataset.confirm; const id=$("#modalLayer").dataset.trackId; const path=$("#modalLayer").dataset.path; const backupId=$("#backupChoice")?.value||$("#modalLayer").dataset.backupId; if(kind==="delete-playlist") await call("deletePlaylist",{playlistId:state.playlist.id}); else if(kind==="remove-root") await call("removeRoot",{path}); else if(kind==="clear-queue") await call("clearQueue"); else if(kind==="restore-tags") await call("restoreTags",{id,backupId}); closeModal(); await renderView(); return; }
  if(e.target.closest("[data-modal-close]")||e.target===$("#modalClose")){closeModal();return;}
  if(e.target.closest("[data-lyric-result]")){const index=Number(e.target.closest("[data-lyric-result]").dataset.lyricResult);const results=JSON.parse($("#modalLayer").dataset.lyrics||"[]");const selected=results[index];if(selected) await editLyrics(selected.syncedLyrics||selected.plainLyrics||"");return;}
  if(!e.target.closest("#contextMenu")) $("#contextMenu").classList.remove("show");
});

document.addEventListener("contextmenu",e=>{const row=e.target.closest("[data-context=track]");if(row){e.preventDefault();contextMenu(e.clientX,e.clientY,row.dataset.contextId||row.dataset.track);}});
document.addEventListener("keydown",async e=>{
  if((e.ctrlKey||e.metaKey)&&e.key.toLowerCase()==="k"){e.preventDefault();$("#globalSearch").focus();return;}
  if(e.code==="Space"&&!e.repeat&&!/INPUT|TEXTAREA|SELECT|BUTTON|A/.test(document.activeElement.tagName)&&!document.activeElement.isContentEditable){e.preventDefault();await call("playPause");await refreshCurrent();return;}
  if((e.altKey||e.metaKey)&&e.key==="ArrowLeft"){e.preventDefault();await call("previous");return;}
  if((e.altKey||e.metaKey)&&e.key==="ArrowRight"){e.preventDefault();await call("next");return;}
  if(e.key==="Enter"&&e.target.matches("tr[data-track]")){await action("play-track",{dataset:{id:e.target.dataset.track}});}
  if(e.key==="Enter"&&e.target.matches("[data-open-type]"))e.target.click();
  if(e.key==="Escape"){closeModal();$("#contextMenu").classList.remove("show");}
});

$("#globalSearch").addEventListener("focus",()=>{if(!["Search","Playlist","Duplicates"].includes(state.view))navigate("Search");});
$("#globalSearch").addEventListener("input",e=>{
  if(!["Playlist","Duplicates"].includes(state.view))state.view="Search";
  state.search=e.target.value;state.offset=0;if(state.view==="Duplicates")state.duplicateOffset=0;
  const view=state.view,query=state.search;clearTimeout(searchTimer);searchTimer=setTimeout(async()=>{
    if(view!==state.view)return;
    await call("setView",{view,search:query,playlistId:state.playlist?.id||null});await renderView();
  },220);
});
$("#backBtn").addEventListener("click",()=>{if(state.historyIndex>0){state.historyIndex--;navigate(state.history[state.historyIndex],false);}});
$("#forwardBtn").addEventListener("click",()=>{if(state.historyIndex<state.history.length-1){state.historyIndex++;navigate(state.history[state.historyIndex],false);}});
$("#scanBtn").addEventListener("click",()=>action("scan")); $("#scanPause").addEventListener("click",()=>call("toggleScanPause")); $("#scanCancel").addEventListener("click",()=>call("cancelScan"));
$("#playBtn").addEventListener("click",async()=>{await call("playPause");await refreshCurrent();}); $("#prevBtn").addEventListener("click",async()=>{await call("previous");await refreshCurrent();}); $("#nextBtn").addEventListener("click",async()=>{await call("next");await refreshCurrent();});
$("#shuffleBtn").addEventListener("click",async()=>{await call("toggleShuffle");await refreshCurrent();}); $("#repeatBtn").addEventListener("click",async()=>{await call("cycleRepeat");await refreshCurrent();}); $("#abBtn").addEventListener("click",async()=>{await call("toggleAbRepeat");await refreshCurrent();}); $("#heartBtn").addEventListener("click",()=>action("favorite",{dataset:{id:state.track?.id||""}}));
$("#progressRange").addEventListener("change",async e=>{await call("seek",{seconds:Number(e.target.value)});}); $("#volumeRange").addEventListener("input",e=>setVolume(e.target.value)); $("#muteBtn").addEventListener("click",toggleMute);
function setPanelOpen(open){state.panelOpen=!!open;const root=$("#appRoot");root.classList.toggle("panel-closed",!state.panelOpen);root.classList.toggle("panel-open",state.panelOpen);$("#panelToggleBtn").setAttribute("aria-label",state.panelOpen?"Hide Now Playing panel":"Show Now Playing panel");$("#panelToggleBtn").title=state.panelOpen?"Hide Now Playing panel":"Show Now Playing panel";}
function updateScanPresentation(scan) {
  state.scan=scan||null;
  const active=!!scan?.active;
  const details=`${scan?.paused?"Paused · ":active?"Scanning · ":""}${scan?.filesFound||0} tracks · ${scan?.directoriesVisited||0} folders${scan?.currentPath?` · ${scan.currentPath}`:""}`;
  const strip=$("#scanStrip");
  strip.hidden=!active;
  $("#scanText").textContent=details;
  $("#scanPause").textContent=scan?.paused?"Resume":"Pause";
  const folderStatus=$("#folderScanStatus");
  if(folderStatus) {
    folderStatus.hidden=!active;
    if(active) {
      $("#folderScanTitle").textContent=scan.paused?"Scan paused":"Scanning library";
      $("#folderScanDetails").textContent=details;
    }
  }
  const homeEmpty=$("#homeLibraryEmpty");
  if(homeEmpty) {
    const hasRoots=homeEmpty.dataset.hasRoots==="true";
    $("#homeEmptyTitle").textContent=active?(scan.paused?"Scan paused":"Scanning your music folders"):hasRoots?"No indexed tracks yet":"No music folders added";
    $("#homeEmptyMessage").textContent=active?details:hasRoots?"Scan your selected folders to add tracks to the library.":"Add a folder to scan your local collection.";
    const action=$("#homeEmptyAction");
    action.hidden=active;
    action.dataset.action=hasRoots?"scan":"add-folder";
    action.textContent=hasRoots?"Scan folders":"Add music folder";
  }
}
$("#panelClose").addEventListener("click",()=>setPanelOpen(false));$("#panelToggleBtn").addEventListener("click",()=>setPanelOpen(!state.panelOpen));$$('.panel-tabs [data-panel]').forEach(b=>b.addEventListener("click",async()=>{state.panel=b.dataset.panel;updatePanel();await call("setPanelMode",{mode:state.panel});state.settings.rightSidebarMode=state.panel==="info"?"Info":"Queue";}));
$("#routeView").addEventListener("change",async e=>{if(e.target.id==="sortSelect"){state.sort=e.target.value;state.offset=0;await renderView();}if(e.target.id==="duplicateSort"){state.duplicateSort=e.target.value;state.duplicateOffset=0;await renderView();}if(e.target.id==="outputDevice")await call("setAudioDevice",{id:e.target.value});if(e.target.id==="crossfade")await call("setCrossfade",{seconds:Number(e.target.value)});if(e.target.id==="eqPreset")await call("setEqualizerPreset",{name:e.target.value});if(e.target.matches("[data-setting]")){const s={};s[e.target.dataset.setting]=e.target.value;await call("updateSettings",{settings:s});state.settings={...state.settings,...s};setTheme(state.settings);}});
$("#routeView").addEventListener("input",async e=>{if(e.target.matches("[data-eq]")){e.target.nextElementSibling.textContent=`${Number(e.target.value).toFixed(1)} dB`;await call("setEqualizerBand",{index:Number(e.target.dataset.eq),value:Number(e.target.value)});}if(e.target.id==="audioVolume")await setVolume(e.target.value);});
$("#routeView").addEventListener("click",async e=>{const sort=e.target.closest("[data-sort]");if(sort){state.sort=sort.dataset.sort;state.offset=0;await renderView();}const toggle=e.target.closest("[data-setting].toggle");if(toggle){toggle.classList.toggle("on");toggle.setAttribute("aria-checked",toggle.classList.contains("on"));const s={};s[toggle.dataset.setting]=toggle.classList.contains("on");await call("updateSettings",{settings:s});state.settings={...state.settings,...s};setTheme(state.settings);return;}const move=e.target.closest("[data-nav-move]");if(move){const views=[...($$("#navigation .nav-item[data-view]").map(item=>item.dataset.view))];const order=[...(state.settings.navigationOrder||[]),...views.filter(v=>!(state.settings.navigationOrder||[]).includes(v))];const index=order.indexOf(move.dataset.navView),next=index+Number(move.dataset.navMove);if(next>=0&&next<order.length){[order[index],order[next]]=[order[next],order[index]];state.settings.navigationOrder=order;applyNavigationSettings(state.settings);$(".nav-settings").innerHTML=navigationSettingsMarkup(state.settings);}return;}const visible=e.target.closest("[data-nav-visible]");if(visible){const view=visible.dataset.navVisible;const hidden=new Set(state.settings.hiddenPanels||[]);if(hidden.has(view))hidden.delete(view);else hidden.add(view);state.settings.hiddenPanels=[...hidden];applyNavigationSettings(state.settings);$(".nav-settings").innerHTML=navigationSettingsMarkup(state.settings);}});
$("#routeView").addEventListener("click",async e=>{
  const tab=e.target.closest("[data-settings-nav]");
  if(tab){$$('[data-settings-nav]').forEach(item=>item.classList.toggle("active",item===tab));const section=document.querySelector('[data-settings-section="'+tab.dataset.settingsNav+'"]');section?.scrollIntoView({behavior:"smooth",block:"start"});return;}
  const dot=e.target.closest("[data-accent-color]");
  if(dot){const settings={accentColor:dot.dataset.accentColor,accentManual:true};state.settings={...state.settings,...settings};setTheme(state.settings);$$(".accent-dot").forEach(item=>item.classList.toggle("active",item===dot));const toggle=$('[data-setting="accentManual"]');if(toggle){toggle.classList.add("on");toggle.setAttribute("aria-checked","true");}await call("updateSettings",{settings});}
});
$("#modalClose").addEventListener("click",closeModal); $("#modalLayer").addEventListener("click",e=>{if(e.target===$("#modalLayer"))closeModal();});

onEvent((name,data)=>{
  if(name==="playbackStateChanged"){Object.assign(state,data);updatePlayer();}
  else if(name==="trackChanged"){state.track=data.track||null;Object.assign(state,data);if(state.settings.autoOpenPanel&&window.innerWidth>900)setPanelOpen(true);updatePlayer();updatePanel();loadTrackDetails(state.track);if(state.view==="Lyrics")renderView();}
  else if(name==="queueChanged"){state.queue=data.entries||[];state.queueOffset=data.queueOffset||0;state.queueTotal=data.totalCount||0;state.queueIndex=data.queueIndex??-1;updatePanel();if(state.view==="Queue")renderView();}
  else if(name==="favoriteChanged"){if(state.track?.id===data.id)state.track.favorite=!!data.favorite;updatePlayer();updatePanel();}
  else if(name==="scanChanged")updateScanPresentation(data);
  else if(name==="libraryChanged"){if(["Home","Folders","Playlists","Albums","Artists","Genres","Favorites","Most Played","Recently Played","Recently Added","Songs","Search"].includes(state.view))renderView();}
  else if(name==="settingsChanged"){state.settings={...state.settings,...data};setTheme(state.settings);}
  else if(name==="artworkAccentChanged"){state.settings.artworkAccent=data.color||"";setTheme(state.settings);}
  else if(name==="notification")toast(data.message||"");
  else if(name==="updateAvailable")openModal(`Music Player ${esc(data.tag||"")} is available`,"Updates are opened in your browser and are never installed automatically.",`<button class="action" data-modal-close>Later</button><button class="action primary" data-action="open-release" data-url="${esc(data.url||"")}">View release</button>`);
});

async function start() {
  paintIcons();
  $$("#navigation .nav-item[data-view]").forEach(item=>{const label=item.querySelector("span:not([data-icon])")?.textContent?.trim();if(label){item.setAttribute("aria-label",label);item.title=label;}});
  try { const data=await call("getBootstrap"); state.track=data.track||null; state.playing=!!data.playing; state.position=data.positionSeconds||0; state.duration=data.durationSeconds||0; state.volume=data.volume??75;state.lastVolume=state.volume||75;state.muted=state.volume===0; state.shuffle=!!data.shuffle; state.repeat=data.repeat||"Off"; state.repeatA=data.repeatA??null; state.repeatB=data.repeatB??null; state.queue=data.queue||[];state.queueOffset=data.queueOffset||0;state.queueTotal=data.queueTotal||0;state.queueIndex=data.queueIndex??-1;state.panel=data.panel||"queue";state.panelOpen=data.panelOpen??(window.innerWidth>1180);state.settings={...(data.settings||{}),resolvedTheme:data.resolvedTheme||"Dark"};state.scan=data.scan||null;setTheme(state.settings);setPanelOpen(state.panelOpen);state.view=data.view||"Home";await renderView();updateScanPresentation(state.scan);loadTrackDetails(state.track);if(data.updateAvailable)openModal(`Music Player ${esc(data.updateAvailable.tag||"")} is available`,"Updates are opened in your browser and are never installed automatically.",`<button class="action" data-modal-close>Later</button><button class="action primary" data-action="open-release" data-url="${esc(data.updateAvailable.url||"")}">View release</button>`); }
  catch(error) { $("#routeView").innerHTML=`<div class="empty-state"><b>Music Player could not connect to Windows</b>${esc(error.message)}</div>`; }
}
start();
