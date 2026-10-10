# Music Player interface

The WinUI 3 window hosts the locally packaged HTML, CSS, and JavaScript interface through WebView2. `local_music_player_v2.html` was temporary visual input and is the migration's reference; it now sits outside the checkout in the parent Projects folder. The older `local_music_player.html` reference was superseded. Neither file is an application resource, build input, or release asset. The product name is **Music Player** and is independent of names shown inside a visual reference.

## Visual system

Use the near-black background hierarchy, flat compact navigation, flexible library area, and contextual Now Playing pane. Keep the restrained lime accent, compact type, dense song rows, small-radius artwork, hover-revealed play actions, and persistent bottom player. Avoid marketing copy, decorative statistics, blanket borders, and generic dashboard cards.

Prefer embedded or local artwork. For missing art, use a stable gradient derived from album or artist identity with initials; never treat fallback art as library metadata.

## Layout and behavior

At desktop widths keep the navigation rail, main library, right pane, and bottom player visible. At narrower widths let the right pane yield first, compact the navigation, reduce card columns, and hide secondary table columns while keeping playback and queue access reachable.

Home and detail pages use real library content. Search uses one field. Songs, albums, artists, playlists, folders, queue, lyrics, duplicates, audio controls, and settings extend the same visual system.

C# owns playback, library and playlist state, scanning, settings, filesystem access, and Windows services. The WebView bridge accepts explicit allowlisted commands and returns bounded, paged data. Artwork is lazy-loaded and cached. Playback updates change player controls without rebuilding library views.

The local frontend is vanilla JavaScript: `scripts/api.js` carries correlated bridge commands and events, `scripts/library.js` renders library/detail views, `scripts/player.js` renders authoritative playback state, and `scripts/app.js` wires navigation and interactions. `styles/tokens.css`, `layout.css`, and `components.css` define the shared visual system.

## Accessibility and errors

Keep keyboard access, visible focus, accessible names for icon controls, and respect for reduced motion. Empty libraries, missing artwork/files, scan errors, empty queues, and unavailable audio devices use compact actionable states in the same interface.
