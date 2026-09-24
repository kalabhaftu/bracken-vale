# Audio and tag format matrix

The playback backend is bundled LibVLC. The list below records formats the scanner recognizes; it does not assert that every file variant, codec, metadata field or device configuration has passed Windows playback tests.

| File extension | Indexed | Playback status |
|---|---:|---|
| MP3, WAV/WAVE, FLAC | Yes | Pending Windows validation |
| AAC, M4A, MP4 audio | Yes | Pending Windows validation |
| Ogg, OGA, Opus | Yes | Pending Windows validation |
| WMA, APE, WV, TTA, MPC | Yes | Pending Windows validation |
| AIFF/AIF, DSF, DFF | Yes | Pending Windows validation |

TagLib# writes common title, artist, album, album artist, genre, year, track number, lyrics and cover art fields when supported by the container. Custom fields are currently written through Xiph comments where available. Format-specific tag preservation and artwork compatibility require per-format validation before stable release.

Playback support depends on the bundled LGPL package and its modules. Codec patent rules vary by country; users and distributors are responsible for checking local requirements. No GPL-only LibVLC package is used.
