# Third-party notices

Bracken Vale bundles or restores the following third-party software. Each component remains under its own license; this notice does not replace the license text shipped with the corresponding package.

| Component | Use | License | Source |
|---|---|---|---|
| LibVLC | Local media playback engine and codecs included by the Windows package | LGPL-2.1-or-later; bundled modules and plugins may carry additional notices | [VideoLAN VLC](https://code.videolan.org/videolan/vlc) |
| LibVLCSharp | .NET binding for LibVLC | LGPL-2.1-or-later | [LibVLCSharp](https://github.com/videolan/libvlcsharp) |
| TagLib# | Audio metadata reading and writing | LGPL-2.1-or-later | [TagLib#](https://github.com/mono/taglib-sharp) |
| Microsoft.Data.Sqlite and SQLitePCLRaw | Local SQLite access | MIT; SQLite engine is public domain | [Microsoft.Data.Sqlite](https://github.com/dotnet/efcore), [SQLite](https://www.sqlite.org/copyright.html) |
| Windows App SDK | Native WinUI 3 application framework | MIT | [Windows App SDK](https://github.com/microsoft/WindowsAppSDK) |
| Windows SDK build tools | Build-time WinUI/XAML targets | Microsoft license terms | [Windows SDK Build Tools](https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads) |
| xUnit | Automated tests only; not included in the app | Apache-2.0 | [xUnit](https://github.com/xunit/xunit) |

The end-user app includes the [LGPL 2.1 license text](third-party-licenses/LGPL-2.1.txt), this notice and the MIT project license. Before stable release, verify module-specific notices in the exact bundled LibVLC package and include them in the release. GPL-only LibVLC packages are excluded. See the official [GNU LGPL 2.1 text](https://www.gnu.org/licenses/old-licenses/lgpl-2.1.html) and [VLC third-party licenses](https://www.videolan.org/legal.html).

Trademark and product names belong to their respective owners. No endorsement is implied.
