export function createLibraryViews({state,$,$$,svg,paintIcons,esc,initials,cover,titleOf,subOf,fmtDuration,bytesLabel,call,toast,setTheme,applyNavigationSettings,openModal,updatePlayer,updatePanel}) {
function trackRow(track,index) {
  const id=esc(track.id); const album=esc(track.album||""); const artist=esc(track.artist||"");
  const remove=state.view==="Playlist"&&Number.isInteger(track.position)?`<button class="ctrl" data-action="remove-playlist-track" data-position="${track.position}" aria-label="Remove from playlist">×</button>`:"";
  return `<tr data-track="${id}" tabindex="0" data-context="track"><td class="index">${index+1}</td><td><div class="song-cell">${cover(track,"song-thumb")}<div class="song-info"><b>${esc(track.title)}</b><span>${artist}</span></div><div class="row-actions"><button class="ctrl heart ${track.favorite?"on":""}" data-action="favorite" data-id="${id}" aria-label="Toggle favorite">${svg("heart")}</button>${remove}<button class="ctrl" data-action="context" data-id="${id}" aria-label="More track actions">⋯</button></div></div></td><td class="album-col">${album}</td><td class="added-col">${esc(track.addedDisplay||"")}</td><td class="year-col">${track.year||""}</td><td class="time">${fmtDuration(track.durationSeconds)}</td></tr>`;
}
function songTable(tracks, start=0) {
  if (!tracks?.length) {
    const empty={
      "Favorites":["No favorites yet","Use the heart on a track to keep it here."],
      "Most Played":["Nothing played often yet","Play music from your library to build this list."],
      "Recently Played":["Nothing played recently","Tracks you play will appear here."],
      "Recently Added":["No recent additions","Newly indexed tracks will appear here."],
      "Album":["No indexed tracks for this album","Rescan its folder if the files are available."],
      "Artist":["No indexed tracks for this artist","Rescan its folders if the files are available."],
      "Genre":["No indexed tracks for this genre","Try another genre or rescan the library."]
    }[state.view]||["No tracks here yet","Music in your indexed folders will appear here."];
    return `<div class="empty-state"><b>${empty[0]}</b>${empty[1]}</div>`;
  }
  const s=state.settings;const classes=[s.showArtwork===false?"no-artwork":"",s.showArtist===false?"no-artists":"",s.showAlbum===false?"no-albums":"",s.showAdded===false?"no-added":"show-added",s.showYear?"show-years":"",s.showDuration===false?"no-duration":"",s.showFavorite===false?"no-favorite":""].filter(Boolean).join(" ");
  return `<div class="table-wrap"><table class="song-table ${classes}"><thead><tr><th class="index">#</th><th><button data-sort="Title">Title</button></th><th class="album-col"><button data-sort="Album">Album</button></th><th class="added-col"><button data-sort="Added">Added</button></th><th class="year-col"><button data-sort="Year">Year</button></th><th class="time"><button data-sort="Duration">Time</button></th></tr></thead><tbody>${tracks.map((t,i)=>trackRow(t,start+i)).join("")}</tbody></table></div>`;
}
function card(item,type="album") {
  const id=esc(item.id||item.name||item.title); const key=esc(item.name||item.title||"");
  if(type==="track") return `<article class="card" tabindex="0" data-action="play" data-id="${id}">${cover(item)}<div class="card-title">${esc(titleOf(item))}</div><div class="card-sub">${esc(subOf(item))}</div><button class="play-fab" data-action="play" data-id="${id}" title="Play">${svg("play")}</button></article>`;
  return `<article class="card" tabindex="0" data-open-type="${type}" data-open-id="${id}" data-open-name="${type==="folder"?id:key}">${cover(item,type==="artist"?"round":"")}<div class="card-title">${esc(titleOf(item))}</div><div class="card-sub">${esc(subOf(item) || `${item.trackCount||item.count||0} tracks`)}</div><button class="play-fab" data-action="play-group" data-type="${type}" data-id="${id}" title="Play">${svg("play")}</button></article>`;
}
function cardGrid(items,type="album") {
  if(items?.length) return `<div class="card-grid">${items.map(item=>card(item,type)).join("")}</div>`;
  const empty=state.view==="Playlists"?["No playlists yet","Create a playlist or import an M3U file."]:
    state.view==="Albums"?["No albums indexed","Add or rescan a music folder to find albums."]:
    state.view==="Artists"?["No artists indexed","Add or rescan a music folder to find artists."]:
    state.view==="Genres"?["No genres indexed","Add or rescan a music folder to find genres."]:
    state.view==="Folders"?["No indexed folders","Add a library root and scan it to see folders."]:
    ["Nothing to show yet","Your library will appear here after a folder is scanned."];
  return `<div class="empty-state"><b>${empty[0]}</b>${empty[1]}</div>`;
}
function section(title,items,type,view) { return `<section class="section"><div class="section-head"><h2>${esc(title)}</h2>${view?`<button data-view="${esc(view)}">See all</button>`:""}</div>${type==="track"?`<div class="mini-row-grid">${(items||[]).map((t,i)=>`<div class="mini-row" data-track="${esc(t.id)}" data-context="track">${cover(t,"mini-cover")}<div class="mini-meta"><b>${esc(t.title)}</b><span>${esc(t.artist)}</span></div><button class="ctrl" data-action="play" data-id="${esc(t.id)}" aria-label="Play">${svg("play")}</button></div>`).join("")}</div>`:cardGrid(items,type||"album")}</section>`; }
function viewHeader(title,subtitle,actions="") { return `<div class="page-head"><div><h1>${esc(title)}</h1><p>${esc(subtitle||"")}</p></div>${actions}</div>`; }
function toolbar(actions="") { return `<div class="toolbar">${actions}</div>`; }

async function navigate(view,push=true,query=undefined) {
  if (push && state.view!==view) { state.history=state.history.slice(0,state.historyIndex+1); state.history.push(view); state.historyIndex=state.history.length-1; }
  state.view=view; state.offset=0; if(!["Album","Artist","Genre","Folder"].includes(view)) state.group=null; if(view!=="Playlist") state.playlist=null; if(query!==undefined) state.search=query;
  const navView=view==="Playlist"?"Playlists":view;
  $$(".nav-item").forEach(item=>item.classList.toggle("active",item.dataset.view===navView));
  $$(".player [data-view]").forEach(item=>item.classList.toggle("active",item.dataset.view===view));
  const content=$("#content"); content.scrollTop=0;
  if(["Songs","Albums","Artists","Genres","Folders","Favorites","Most Played","Recently Played","Recently Added","Playlists","Queue","Audio","Settings","Duplicates","Lyrics","Now Playing"].includes(view)) state.search="";
  if(view!=="Search" && $("#globalSearch").value) { $("#globalSearch").value=""; state.search=""; }
  await call("setView",{view,search:state.search,group:state.group,playlistId:state.playlist?.id||null});
  await renderView();
}

async function renderView(append=false) {
  const root=$("#routeView");
  if(!append) root.innerHTML=`<div class="empty-state"><b>Loading your library</b></div>`;
  try {
    let html="";
    switch(state.view) {
      case "Home": html=await renderHome(); break;
      case "Search": html=await renderSearch(); break;
      case "Songs": case "Favorites": case "Most Played": case "Recently Played": case "Recently Added": html=await renderTracksView(append); break;
      case "Albums": case "Artists": case "Genres": html=await renderGroupsView(append); break;
      case "Folders": html=await renderFolders(append); break;
      case "Playlists": html=await renderPlaylists(); break;
      case "Playlist": html=await renderPlaylistDetail(append); break;
      case "Album": case "Artist": case "Genre": case "Folder": html=await renderGroupDetail(); break;
      case "Queue": html=await renderQueue(append); break;
      case "Now Playing": html=await renderNowPlaying(); break;
      case "Lyrics": html=await renderLyrics(); break;
      case "Audio": html=await renderAudio(); break;
      case "Settings": html=await renderSettings(); break;
      case "Duplicates": html=await renderDuplicates(); break;
      default: html=await renderHome();
    }
    if(append) root.insertAdjacentHTML("beforeend",html); else root.innerHTML=html;
    paintIcons(root); updatePanel(); updatePlayer();
  } catch (error) { if(!append) root.innerHTML=`<div class="empty-state"><b>Could not load this view</b>${esc(error.message)}</div>`; }
}

async function renderHome() {
  const data=await call("getHome",{pageSize:8});
  const quick=(data.quick||[]).slice(0,6); const rec=(data.recent||[]).slice(0,8);
  const recentAlbums=[...new Map(rec.filter(track=>track.album).map(track=>[`${track.album}\0${track.albumArtist||track.artist}`,{...track,id:track.album,name:track.album,title:track.album,artist:track.albumArtist||track.artist,album:track.album}])).values()].slice(0,5);
  const playlistData=await call("getPlaylists"); let libraryCards=(playlistData.playlists||[]).slice(0,5); let libraryType="playlist",libraryView="Playlists";
  if(!libraryCards.length){const albums=await call("getGroups",{column:"album",offset:0,pageSize:5});libraryCards=albums.groups||[];libraryType="album";libraryView="Albums";}
  $("#librarySize").textContent=bytesLabel(data.totalBytes);
  const hour=new Date().getHours(),greeting=hour<12?"Good morning":hour<18?"Good afternoon":"Good evening";
  const subtitle=rec.length?"Pick up where you left off.":data.totalTracks?`${Number(data.totalTracks).toLocaleString()} tracks in your local library`:"Your music library";
  let html=viewHeader(greeting,subtitle);
  html=html.replace('class="page-head"','class="page-head home-head"');
  if(!data.totalTracks) {
    const folderData=await call("getFolders",{rootsOnly:true}),hasRoots=!!folderData.roots?.length,scanning=!!folderData.scan?.active;
    state.homeHasRoots=hasRoots;
    const title=hasRoots?(scanning?"Scanning your music folders":"No indexed tracks yet"):"No music folders added";
    const message=hasRoots?(scanning?"Tracks will appear here as the scan indexes your files.":"Scan your selected folders to add tracks to the library."):"Add a folder to scan your local collection.";
    const action=scanning?"hidden":"";
    return html+`<div class="empty-state" id="homeLibraryEmpty" data-has-roots="${hasRoots}"><b id="homeEmptyTitle">${title}</b><span id="homeEmptyMessage">${message}</span><button class="action primary empty-action" id="homeEmptyAction" data-action="${hasRoots?"scan":"add-folder"}" ${action}>${hasRoots?"Scan folders":"Add music folder"}</button></div>`;
  }
  html+=section("Quick access",quick,"track","Songs");
  if(recentAlbums.length) html+=`<section class="section"><div class="section-head"><h2>Recently played</h2><button data-view="Recently Played">See all</button></div>${cardGrid(recentAlbums,"album")}</section>`;
  if(libraryCards.length) html+=`<section class="section"><div class="section-head"><h2>Made from your library</h2><button data-view="${libraryView}">View all</button></div>${cardGrid(libraryCards,libraryType)}</section>`;
  else if(rec.length) html+=`<section class="section"><div class="section-head"><h2>Recently played</h2><button data-view="Recently Played">See all</button></div>${cardGrid(rec.slice(0,5),"track")}</section>`;
  return html;
}

async function renderSearch() {
  const query=state.search.trim(),filter=state.filter;let html=viewHeader("Search",query?`Results in your local library for “${query}”`:"Find songs, artists, albums, and playlists.");
  if(!query) return html+`<div class="empty-state"><b>Search your library</b>Type in the single search field above to get started.</div>`;
  if(state.searchQuery!==query){state.searchQuery=query;state.searchPages={songs:0,albums:0,artists:0,playlists:0};state.searchResults={};}
  const definitions={songs:{filter:"song",label:"Songs",kind:"track",pageSize:40},albums:{filter:"album",label:"Albums",kind:"album",pageSize:24},artists:{filter:"artist",label:"Artists",kind:"artist",pageSize:24},playlists:{filter:"playlist",label:"Playlists",kind:"playlist",pageSize:20}};
  const selected=filter==="all"?Object.keys(definitions):[{song:"songs",album:"albums",artist:"artists",playlist:"playlists"}[filter]].filter(Boolean);
  const pages=await Promise.all(selected.map(async key=>{const def=definitions[key],page=state.searchPages[key]||0;const data=await call("search",{query,filter:def.filter,offset:page*def.pageSize,pageSize:def.pageSize});return [key,data];}));
  if(query!==state.search.trim()||filter!==state.filter||state.view!=="Search")return "";
  for(const [key,data] of pages)state.searchResults[key]=data;
  html+=`<div class="pill-row">${["all","song","album","artist","playlist"].map(f=>`<button class="chip ${filter===f?"active":""}" data-filter="${f}">${f==="all"?"All":`${f[0].toUpperCase()}${f.slice(1)}s`}</button>`).join("")}</div>`;
  let any=false;
  for(const key of selected){const def=definitions[key],data=state.searchResults[key]||{items:[],totalCount:0},items=data.items||[],total=data.totalCount||0,page=state.searchPages[key]||0,totalPages=Math.max(1,Math.ceil(total/def.pageSize));if(!items.length)continue;any=true;
    html+=`<section class="section"><div class="section-head"><h2>${def.label}</h2><span class="muted">${Number(total).toLocaleString()}</span></div>${key==="songs"?songTable(items,page*def.pageSize):cardGrid(items,def.kind)}${totalPages>1?`<div class="pagination"><button class="action" data-action="search-page" data-type="${key}" data-direction="-1" ${page===0?"disabled":""}>Previous</button><span class="muted">Page ${page+1} of ${totalPages}</span><button class="action" data-action="search-page" data-type="${key}" data-direction="1" ${page+1>=totalPages?"disabled":""}>Next</button></div>`:""}</section>`;
  }
  if(!any)html+=`<div class="empty-state"><b>No results for “${esc(query)}”</b>Try another song, artist, album, or playlist name.</div>`;
  return html;
}

function viewFilter(view) { return ({"Favorites":"favorites","Most Played":"most-played","Recently Played":"recent","Recently Added":"recently-added"})[view]||null; }
async function renderTracksView(append=false) {
  const page=await call("getTracks",{view:state.view,search:state.search,sort:state.sort,descending:state.descending,offset:state.offset,pageSize:state.pageSize});
  const pageTracks=page.tracks||[];const previousCount=append?state.items.length:0;
  state.total=page.totalCount||0; state.items=append?[...state.items,...pageTracks]:pageTracks;
  const subtitle=`${Number(state.total).toLocaleString()} tracks`;
  let html=append?"":viewHeader(state.view,subtitle,`<button class="action" data-action="play-view">Play all</button><button class="action" data-action="shuffle-view">Shuffle</button>`)+toolbar(`<label class="muted">Sort</label><select class="select" id="sortSelect">${["Title","Artist","Album","Genre","Year","Added","Duration","PlayCount","LastPlayed","Path","Rating"].map(x=>`<option ${state.sort===x?"selected":""}>${x}</option>`).join("")}</select><button class="action" data-action="direction">${state.descending?"Descending":"Ascending"}</button>`);
  html+=songTable(append?pageTracks:state.items,previousCount);
  if(state.items.length<state.total) html+=`<button class="action load-more" data-action="load-more">Load more tracks</button>`;
  return html;
}

async function renderGroupsView(append=false) {
  const column=({Albums:"album",Artists:"artist",Genres:"genre"})[state.view];
  const data=await call("getGroups",{column,offset:state.offset,pageSize:60,search:state.search});
  const pageGroups=data.groups||[];state.total=data.totalCount||0; state.items=append?[...state.items,...pageGroups]:pageGroups;
  const html=(append?"":viewHeader(state.view,`${Number(state.total).toLocaleString()} ${state.view.toLowerCase()}`))+cardGrid(append?pageGroups:state.items,column)+ (state.items.length<state.total?`<button class="action load-more" data-action="load-more">Load more</button>`:"");
  return html;
}

async function renderFolders(append=false) {
  const offset=append?(state.folderOffset||0)+50:0;const data=await call("getFolders",{offset,pageSize:50});const roots=data.roots||[];const status=data.scan||null;
  state.folderItems=append?[...(state.folderItems||[]),...(data.folders||[])]:data.folders||[];state.folderOffset=data.folderOffset||0;state.folderCount=data.folderCount||0;
  if(append)return cardGrid(state.folderItems.slice(-50),"folder")+(state.folderOffset+50<state.folderCount?`<button class="action load-more" data-action="load-folders">Load more folders</button>`:"");
  return viewHeader("Music folders","Choose where Music Player should look for local audio.",`<button class="action primary" data-action="add-folder">＋ Add folder</button><button class="action" data-action="scan">Scan now</button><button class="action" data-action="manage-roots">Manage folders</button><button class="action" data-action="manage-exclusions">Scan exclusions</button>`)
    +`<div class="folder-row scan-status" id="folderScanStatus" ${status?.active?"":"hidden"}><span class="scan-dot"></span><div><b id="folderScanTitle">${status?.paused?"Scan paused":"Scanning library"}</b><span id="folderScanDetails">${esc(status?.currentPath||"")} · ${status?.filesFound||0} tracks · ${status?.directoriesVisited||0} folders</span></div></div>`
    +`<section class="section"><div class="section-head"><h2>Indexed folders</h2><span class="muted">${Number(state.folderCount).toLocaleString()}</span></div>${cardGrid(state.folderItems,"folder")}</section>`
    +(state.folderOffset+50<state.folderCount?`<button class="action load-more" data-action="load-folders">Load more folders</button>`:"")
    +`<section class="section"><div class="section-head"><h2>Library roots</h2><button data-action="manage-roots">Manage</button></div><div class="folder-grid">${roots.map(root=>`<div class="folder-row"><div class="folder-icon"></div><div style="min-width:0;flex:1"><b title="${esc(root.path)}">${esc(root.name||root.path)}</b><span>${esc(root.path)} · ${Number(root.trackCount||0).toLocaleString()} tracks</span></div><button class="action" data-action="remove-root" data-path="${esc(root.path)}">Remove</button></div>`).join("")}</div>${roots.length?"":`<div class="empty-state"><b>No music folders added</b>Add a folder to build your local library.</div>`}</section>`;
}

async function renderPlaylists() {
  const data=await call("getPlaylists"); const lists=data.playlists||[];
  return viewHeader("Playlists","Your playlists are stored on this PC.",`<button class="action" data-action="import-playlist">Import M3U / M3U8</button><button class="action primary" data-action="new-playlist">＋ New playlist</button>`)+cardGrid(lists,"playlist");
}

async function renderPlaylistDetail(append=false) {
  if(!state.playlist) return renderPlaylists();
  const page=await call("getPlaylistTracks",{playlistId:state.playlist.id,search:state.search,offset:state.offset,pageSize:state.pageSize});
  state.total=page.totalCount||0; state.items=page.tracks||[];
  const listing=state.items.length?songTable(state.items,state.offset):`<div class="empty-state"><b>${state.search?"No tracks match this search":"This playlist is empty"}</b>${state.search?"Try another title, artist, or album.":"Add tracks from your library to get started."}</div>`;
  const pageContent=listing+(state.offset+state.items.length<state.total?`<button class="action load-more" data-action="load-more">Load more tracks</button>`:"");
  return append?pageContent:viewHeader(state.playlist.name,`${Number(state.total).toLocaleString()} ${state.search?"matching ":""}tracks`,toolbar(`<button class="action primary" data-action="play-playlist">Play</button><button class="action" data-action="shuffle-playlist">Shuffle</button><button class="action" data-action="rename-playlist">Rename</button><button class="action" data-action="export-playlist">Export</button><button class="action danger" data-action="delete-playlist">Delete</button>`))+pageContent;
}

async function renderGroupDetail() {
  const group=state.group; if(!group) return navigate(state.view==="Album"?"Albums":state.view==="Artist"?"Artists":"Genres",false);
  const detailSort=group.column==="artist"?"PlayCount":group.column==="album"?"TrackNumber":state.sort;
  const detailDescending=group.column==="artist"?true:group.column==="album"?false:state.descending;
  const page=await call("getTracks",{view:state.view,groupColumn:group.column,groupValue:group.name,offset:state.offset,pageSize:state.pageSize,sort:detailSort,descending:detailDescending});
  state.total=page.totalCount||0; state.items=page.tracks||[];
  const sample=state.items[0]||group;let albums=[];if(group.column==="artist"){const a=await call("getArtistAlbums",{artist:group.name,pageSize:30});albums=a.groups||[];}
  const tracksTitle=group.column==="artist"?"Popular tracks":"Tracks";
  return `<div class="detail-hero">${cover({...sample,name:group.name,album:group.column==="album"?group.name:sample.album,artist:group.column==="artist"?group.name:sample.artist},group.column==="artist"?"round":"")}<div><div class="eyebrow">${esc(group.column)}</div><h1>${esc(group.name)}</h1><p>${Number(state.total).toLocaleString()} tracks${sample.year?` · ${sample.year}`:""}</p><div class="toolbar"><button class="action primary" data-action="play-view">Play</button><button class="action" data-action="shuffle-view">Shuffle</button></div></div></div>${albums.length?section("Albums",albums,"album"):""}<section class="section"><div class="section-head"><h2>${tracksTitle}</h2></div>${songTable(state.items)}</section>`;
}

async function renderQueue(append=false) {
  const data=await call("getQueue",{offset:state.offset,pageSize:200}); state.queueItems=data.entries||[]; state.queuePageOffset=state.offset; state.queueTotal=data.totalCount||0; state.queueIndex=data.queueIndex??-1;
  let html=append?"":viewHeader("Queue",`${state.queueTotal} queued tracks`,toolbar(`<button class="action" data-action="clear-queue">Clear upcoming</button>`));
  html+=state.queueItems.length?`<div class="table-wrap"><table class="song-table"><thead><tr><th class="index">#</th><th>Title</th><th class="album-col">Artist</th><th class="time">Actions</th></tr></thead><tbody>${state.queueItems.map((t,i)=>{const index=state.queuePageOffset+i;return `<tr data-queue-index="${index}" data-context="track" data-context-id="${esc(t.id)}"><td class="index">${index===state.queueIndex?"▶":index+1}</td><td><div class="song-cell">${cover(t,"song-thumb")}<div class="song-info"><b>${esc(t.title)}</b><span>${esc(t.artist)}</span></div></div></td><td>${esc(t.artist)}</td><td class="time"><button class="action" data-action="play-queue" data-index="${index}">▶</button> <button class="action" data-action="queue-up" data-index="${index}">↑</button> <button class="action" data-action="queue-down" data-index="${index}">↓</button> <button class="action" data-action="queue-remove" data-index="${index}">×</button></td></tr>`;}).join("")}</tbody></table></div>`:`<div class="empty-state"><b>Queue is empty</b>Songs you add will appear here.</div>`;
  if(state.queuePageOffset+state.queueItems.length<state.queueTotal) html+=`<button class="action load-more" data-action="load-more">Load more queued tracks</button>`;
  return html;
}

async function renderNowPlaying() {
  const data=await call("getCurrentTrack"); state.track=data.track||null;
  if(!state.track) return viewHeader("Now Playing","Nothing is playing.")+`<div class="empty-state"><b>Choose a song to begin</b>Playback stays active as you move between views.</div>`;
  return `<div class="detail-hero now-detail">${cover(state.track)}<div><div class="eyebrow">Now playing</div><h1>${esc(state.track.title)}</h1><p>${esc(state.track.artist)}${state.track.album?` · ${esc(state.track.album)}`:""}</p><div class="toolbar"><button class="action" data-action="favorite" data-id="${esc(state.track.id)}">${state.track.favorite?"♥ Favorited":"♡ Favorite"}</button><button class="action" data-action="show-location" data-id="${esc(state.track.id)}">Show in folder</button><button class="action" data-action="details" data-id="${esc(state.track.id)}">Track details</button></div></div></div>`;
}

async function renderLyrics() {
  const data=await call("getLyrics",{id:state.trackId||state.track?.id}); state.track=data.track||null; state.lyricLines=data.lines||[];state.lyricText=data.plainText||"";
  if(!state.track) return viewHeader("Lyrics","Choose a song to view lyrics.")+`<div class="empty-state"><b>No track selected</b>Lyrics appear here when a track is playing.</div>`;
  const lyrics=state.lyricLines.length?state.lyricLines.map((line,i)=>`<p data-lyric-index="${i}" class="${line.active?"active":""}">${esc(line.text)}</p>`).join(""):state.lyricText?`<div class="lyrics-plain">${esc(state.lyricText)}</div>`:`<div class="empty-state"><b>No lyrics found</b>Lyrics may be embedded, stored beside the file, or searched on LRCLIB when you ask.</div>`;
  return viewHeader("Lyrics",`${state.track.title} · ${state.track.artist}`,toolbar(`<button class="action" data-action="edit-lyrics">Edit lyrics</button><button class="action" data-action="search-lyrics">Search LRCLIB</button>`))+`<div class="lyrics-lines">${lyrics}</div>`;
}

async function renderAudio() {
  const data=await call("getAudioSettings");
  const devices=data.devices||[];
  const deviceControl=devices.length
    ? `<select class="select" id="outputDevice">${devices.map(d=>`<option value="${esc(d.id)}" ${d.id===data.selectedId?"selected":""}>${esc(d.name)}</option>`).join("")}</select>`
    : `<span class="muted">No audio output devices are available. Connect a device and refresh.</span>`;
  return viewHeader("Audio","Playback, output, and equalizer.")+
    `<section class="setting-section"><h2>Volume</h2><div class="setting-row"><div><b>Output level</b><span>Volume is shared with Windows media controls.</span></div><input id="audioVolume" type="range" min="0" max="100" value="${state.volume}" aria-label="Volume"><button class="action" data-action="toggle-mute">${state.muted?"Unmute":"Mute"}</button></div></section>`+
    `<section class="setting-section"><h2>Audio output</h2><p>Choose a connected Windows playback device.</p><div class="setting-row"><div><b>Output device</b><span>Bluetooth headphones and speakers are supported.</span></div>${deviceControl}<button class="action" data-action="refresh-devices">Refresh</button></div></section>`+
    `<section class="setting-section"><h2>Playback transition</h2><div class="setting-row"><div><b>Crossfade</b><span>Blend into the next track.</span></div><select class="select" id="crossfade">${[0,2,3,5,8,10].map(n=>`<option value="${n}" ${n===data.crossfadeSeconds?"selected":""}>${n?`${n} seconds`:"Off"}</option>`).join("")}</select></div></section>`+
    `<section class="setting-section"><h2>10-band equalizer</h2><div class="setting-row"><div><b>Preset</b><span>Built-in and saved curves.</span></div><select class="select" id="eqPreset"><option>Off</option>${(data.presets||[]).map(p=>`<option ${p===data.currentPreset?"selected":""}>${esc(p)}</option>`).join("")}</select><button class="action" data-action="save-eq">Save custom preset</button></div><div class="eq-bands">${(data.bands||[]).map((b,i)=>`<label>${esc(b.label)}<input type="range" min="-20" max="20" step="0.5" value="${Number(b.value)||0}" data-eq="${i}"><span>${Number(b.value||0).toFixed(1)} dB</span></label>`).join("")}</div></section>`;
}

async function renderSettings() {
  const s=await call("getSettings"); state.settings=s; setTheme(s);
  const activeAccent=s.accentManual&&s.accentColor?s.accentColor:s.accentMode==="Artwork"&&s.artworkAccent?s.artworkAccent:"#b7ff2d";
  const row=(key,title,desc,type="toggle",values=[])=>`<div class="setting-row"><div><b>${esc(title)}</b><span>${esc(desc)}</span></div>${type==="toggle"?`<button class="toggle ${s[key]?"on":""}" data-setting="${key}" role="switch" aria-checked="${!!s[key]}"><i></i></button>`:type==="select"?`<select class="select" data-setting="${key}">${values.map(v=>`<option ${String(s[key])===String(v.value)?"selected":""} value="${esc(v.value)}">${esc(v.label)}</option>`).join("")}</select>`:`<input class="field ${type==="color"?"setting-color":""}" ${type==="color"?`type="color"`:"type=\"text\""} data-setting="${key}" value="${esc(s[key]||"")}">`}</div>`;
  return viewHeader("Settings","Choose how Music Player looks and behaves.")+`<div class="settings-grid"><nav class="settings-menu" aria-label="Settings sections"><button class="active" data-settings-nav="appearance">Appearance</button><button data-settings-nav="library">Library</button><button data-settings-nav="playback">Playback</button><button data-view="Audio">Audio</button><button data-settings-nav="advanced">Advanced</button></nav><div class="settings-panel">`+
    `<section class="setting-section settings-anchor" id="settings-appearance" data-settings-section="appearance"><h2>Appearance</h2><p>Match the player to your desktop.</p>${row("theme","Color theme","System, light, or dark.","select",[{value:"System",label:"System"},{value:"Light",label:"Light"},{value:"Dark",label:"Dark"}])}${row("accentMode","Accent style","Keep the Music Player lime or use album artwork.","select",[{value:"Native",label:"Lime"},{value:"Artwork",label:"Album artwork"}])}${row("accentManual","Use a custom accent","Choose a color for active controls.")}${row("accentColor","Custom accent color","Applied when the custom accent is enabled.","color")}${row("windowMaterial","Window material","Native backdrop behind the player.","select",[{value:"Mica",label:"Mica"},{value:"Acrylic",label:"Desktop Acrylic"},{value:"Opaque",label:"Opaque"}])}${row("motionStyle","Navigation motion","Respect accessibility animation preferences.","select",[{value:"Off",label:"Off"},{value:"Subtle",label:"Subtle"},{value:"Expressive",label:"Expressive"}])}<div class="setting-row"><div><b>Accent presets</b><span>Quick choices for the custom accent.</span></div><div class="accent-list">${[["#b7ff2d","Lime"],["#59d7ff","Sky"],["#a878ff","Violet"],["#ff6e91","Rose"],["#ffb04a","Amber"]].map(([color,label])=>`<button class="accent-dot ${activeAccent===color?"active":""}" data-accent-color="${color}" title="${label}" style="background:${color}"></button>`).join("")}</div></div></section>`+
    `<section class="setting-section settings-anchor" id="settings-library" data-settings-section="library"><h2>Library</h2><p>Local folders and indexed tracks.</p>${row("hideDuplicates","Hide exact duplicates","Show one representative in normal library views.")}${["showArtwork","showArtist","showAlbum","showAdded","showYear","showDuration","showFavorite"].map((k,i)=>row(k,["Artwork","Artist","Album","Added date","Year","Duration","Favorite button"][i],"Show this column when the window is wide enough.")).join("")}<div class="toolbar"><button class="action" data-action="manage-roots">Manage folders</button><button class="action" data-action="manage-exclusions">Scan exclusions</button><button class="action" data-action="scan">Scan library</button></div></section>`+
    `<section class="setting-section settings-anchor" id="settings-playback" data-settings-section="playback"><h2>Playback</h2><p>Keep playback state and the queue easy to reach.</p>${row("autoOpenPanel","Open the Now Playing pane when a song starts","Show the current track and upcoming queue.")}<div class="toolbar"><button class="action" data-view="Audio">Audio output and equalizer</button></div></section>`+
    `<section class="setting-section settings-anchor" id="settings-advanced" data-settings-section="advanced"><h2>Advanced</h2><p>Application behavior, navigation, and diagnostics.</p>${row("checkUpdates","Check GitHub releases weekly","Updates are never installed automatically.")}${row("minimizeToTray","Minimize to notification area","Keep the player available from the system tray.")}${row("navigationWidth","Navigation width","Expanded sidebar width in logical pixels.","text")}${row("browseWidth","Browse grid spacing","Spacing between album, artist, and playlist tiles.","text")}<div class="toolbar"><button class="action" data-action="check-updates">Check for updates</button><button class="action" data-action="open-default-apps">Choose default music player</button><button class="action" data-action="open-logs">Open logs</button><button class="action" data-action="export-logs">Export logs</button></div><h3>Navigation order and visibility</h3><p>Move destinations with the arrows and hide optional destinations with the switch.</p><div class="nav-settings">${navigationSettingsMarkup(s)}</div></section><div class="toolbar"><button class="action primary" data-action="save-settings">Save settings</button></div></div></div>`;
}

function navigationSettingsMarkup(settings){const views=$$("#navigation .nav-item[data-view]").map(item=>item.dataset.view);const order=[...(settings.navigationOrder||[]),...views.filter(view=>!(settings.navigationOrder||[]).includes(view))].filter(view=>views.includes(view));const hidden=new Set(settings.hiddenPanels||[]);return order.map((view,index)=>`<div class="setting-row"><b>${esc(view)}</b><div class="toolbar"><button class="action" data-nav-move="-1" data-nav-view="${esc(view)}" ${index===0?"disabled":""}>↑</button><button class="action" data-nav-move="1" data-nav-view="${esc(view)}" ${index===order.length-1?"disabled":""}>↓</button><button class="toggle ${hidden.has(view)?"":"on"}" data-nav-visible="${esc(view)}" role="switch" aria-checked="${!hidden.has(view)}"><i></i></button></div></div>`).join("");}

async function renderDuplicates() {
  const pageSize=100,data=await call("getDuplicates",{offset:state.duplicateOffset,pageSize,search:state.search,sort:state.duplicateSort,descending:state.duplicateDescending}),groups=data.groups||[],total=data.totalCount||0;
  const pages=Math.max(1,Math.ceil(total/pageSize)),page=Math.floor(state.duplicateOffset/pageSize);
  const controls=toolbar(`<label class="muted">Sort</label><select class="select" id="duplicateSort">${["Title","Artist","Album","Year","Added","Duration","PlayCount","LastPlayed","Path","Rating"].map(value=>`<option ${state.duplicateSort===value?"selected":""}>${value}</option>`).join("")}</select><button class="action" data-action="duplicate-direction">${state.duplicateDescending?"Descending":"Ascending"}</button>`);
  const table=groups.length?`<div class="table-wrap"><table class="song-table"><thead><tr><th>Title</th><th>Artist</th><th class="time">Copies</th><th>Locations</th></tr></thead><tbody>${groups.map(g=>`<tr data-duplicate="${esc(g.id)}"><td>${esc(g.title)}</td><td>${esc(g.artist)}</td><td class="time">${g.copyCount}</td><td><button class="action" data-action="duplicate-files" data-id="${esc(g.id)}">Show locations</button></td></tr>`).join("")}</tbody></table></div>`:`<div class="empty-state"><b>${state.search?"No duplicate groups match":"No exact duplicates found"}</b>${state.search?"Try another title, artist, or album.":"Duplicate groups are created as the library is indexed."}</div>`;
  return viewHeader("Duplicates",`${Number(total).toLocaleString()} exact duplicate groups`)+controls+table+(pages>1?`<div class="pagination"><button class="action" data-action="duplicate-page" data-direction="-1" ${page===0?"disabled":""}>Previous</button><span class="muted">Page ${page+1} of ${pages}</span><button class="action" data-action="duplicate-page" data-direction="1" ${page+1>=pages?"disabled":""}>Next</button></div>`:"");
}

async function openDuplicateFiles(id,offset=0) {
  const pageSize=100,data=await call("getDuplicateFiles",{id,offset,pageSize}),files=data.files||[],total=data.totalCount||0,pages=Math.max(1,Math.ceil(total/pageSize)),page=Math.floor(offset/pageSize);
  const body=files.map(file=>`<div class="setting-row"><div><b>${esc(file.title)}</b><span>${esc(file.artist)} · ${esc(file.path)}</span></div><button class="action" data-action="show-file" data-path="${esc(file.path)}">Show in folder</button></div>`).join("")||`<p class="muted">No copies found.</p>`;
  const actions=`${page>0?`<button class="action" data-action="duplicate-files-page" data-id="${esc(id)}" data-offset="${offset-pageSize}">Previous</button>`:""}${page+1<pages?`<button class="action" data-action="duplicate-files-page" data-id="${esc(id)}" data-offset="${offset+pageSize}">Next</button>`:""}<button class="action" data-modal-close>Close</button>`;
  openModal("Duplicate locations",`${body}${pages>1?`<p class="muted">Page ${page+1} of ${pages} · ${Number(total).toLocaleString()} files</p>`:""}`,actions);
}

  return {trackRow,songTable,card,cardGrid,section,viewHeader,toolbar,navigate,renderView,renderHome,renderSearch,viewFilter,renderTracksView,renderGroupsView,renderFolders,renderPlaylists,renderPlaylistDetail,renderGroupDetail,renderQueue,renderNowPlaying,renderLyrics,renderAudio,renderSettings,navigationSettingsMarkup,renderDuplicates,openDuplicateFiles};
}
