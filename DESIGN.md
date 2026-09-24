---
name: Bracken Vale
description: A native Windows music library that keeps listening local.
---

# Design System: Bracken Vale

## Overview

**Creative North Star: "The Listening Shelf"**

Bracken Vale follows the Windows Fluent grammar: clear navigation, legible library rows, familiar command surfaces, and Segoe UI. Album artwork gives the collection its character without replacing the Windows interaction model. Users can switch between a restrained native accent and an artwork-led accent treatment.

The library is the everyday workspace. Playback controls stay in reach, while Now Playing gives the current record a larger, quieter stage. Use the Windows system theme by default; let people choose System, Light, or Dark. Keep the surface accessible when artwork is missing, low contrast, or colorful.

**Key Characteristics:**
- Native WinUI 3 controls and Windows icons
- Segoe UI typography and Windows light/dark resources
- Optional artwork-derived accent, with manual override
- Information-dense library views with an accessible player bar

## Colors

System surfaces and text come from WinUI theme resources. Artwork accent is a user-selectable tint, not a replacement for semantic text, focus, or status colors. A custom accent is chosen by the user.

### Primary
- **System Accent:** Windows `SystemAccentColor`; used for selected navigation, focus, and primary actions.
- **Artwork Accent:** derived from current cover art when the user enables expressive mode; use only for non-semantic emphasis and check contrast before tinting foreground text.

### Neutral
- **Theme Surfaces:** WinUI `ApplicationPageBackgroundThemeBrush`, `CardBackgroundFillColorDefaultBrush`, and `TextFillColorPrimaryBrush`; follow System, Light, or Dark mode.
- **Borders and dividers:** WinUI theme resources, used only where needed to clarify grouping.

## Typography

**Display Font:** Segoe UI Variable (system fallback: Segoe UI)
**Body Font:** Segoe UI Variable (system fallback: Segoe UI)

**Character:** Familiar Windows typography, with restrained size and weight changes that preserve scanning speed across large libraries.

### Hierarchy
- **Headline:** WinUI title styles; page and dialog headings.
- **Title:** WinUI subtitle styles; album, artist, and playlist names.
- **Body:** WinUI body styles; track metadata and settings descriptions.
- **Label:** WinUI caption styles; secondary metadata and technical details.

## Layout

Use a resizable desktop shell with a collapsible, user-arranged navigation/panel rail, a library work area, and a persistent bottom playback bar. Give tables room for sortable metadata columns. The Now Playing view may expand artwork and lyrics while keeping transport controls visible. Preserve keyboard navigation and sensible minimum window dimensions.

## Elevation & Depth

Prefer native tonal layering and WinUI control states. Use Windows 11 materials where available and opaque theme-resource surfaces on Windows 10. Elevation communicates an actual overlay, flyout, or active drag state; do not add decorative glow.

## Shapes

Use WinUI control corner treatments and spacing. Custom surfaces should inherit the Windows theme and remain subordinate to standard controls.

## Components

### Navigation
- Use WinUI `NavigationView` with consistent `SymbolIcon` glyphs. Keep the library destinations discoverable and allow optional panels to be hidden or reordered.

### Library rows
- Use virtualized WinUI list controls for large libraries, sortable headers, keyboard selection, and context actions.
- Artwork thumbnails use a consistent square crop; missing artwork has a quiet native fallback.

### Playback bar
- Keep play/pause, previous/next, timeline, volume, shuffle, and repeat available without opening Now Playing.
- Preserve focus visibility and accessible names for icon-only commands.

## Do's and Don'ts

### Do:
- **Do** use native WinUI controls, Segoe UI, and Windows icon glyphs for common interactions.
- **Do** keep text contrast, keyboard focus, and system theme behavior correct in every accent mode.
- **Do** let cover art influence optional emphasis, never the meaning of status or the legibility of controls.

### Don't:
- **Don't** imitate Windows with a cross-platform web shell or replace familiar controls with bespoke lookalikes.
- **Don't** assume every track has artwork or that album art is licensed sample content.
- **Don't** use blur, accent color, or decorative motion in ways that reduce library readability.
