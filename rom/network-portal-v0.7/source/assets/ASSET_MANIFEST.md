# NICE64 Network Portal v0.5 Asset Manifest

PNG source assets live under `assets/` and are converted by the libdragon
Makefile into `.sprite` files in DragonFS.

Conversion rule:

```text
$(N64_MKSPRITE) --format RGBA16 -o filesystem <png>
mkdfs build/nice-network-pak-client.dfs filesystem
```

The ROM loads sprites with `sprite_load("rom:/<name>.sprite")`.

## Logo

- `assets/icons/Banner.png`
  - Purpose: user-provided high-resolution source art for splash/banner derivatives
- `assets/icons/NICE 64 Logo.png`
  - Purpose: user-provided high-resolution source art for future logo derivatives
- `assets/icons/splash_banner.png`
  - Runtime sprite: `rom:/splash_banner.sprite`
  - Purpose: ROM-ready large splash banner derived from `Banner.png`
- `assets/icons/nice64_header_banner.png`
  - Runtime sprite: `rom:/nice64_header_banner.sprite`
  - Purpose: compact main-menu header banner derived from `Banner.png`
- `assets/logo/nice64_logo.png`
  - Runtime sprite: `rom:/nice64_logo.sprite`
  - Purpose: fallback small NICE64 logo
- `assets/logo/nice64_banner.png`
  - Runtime sprite: `rom:/nice64_banner.sprite`
  - Purpose: larger replaceable banner asset for future layout revisions

## Icons

Menu icon names are supplied by `portal.json` through `bridge://menu` as
`Title|Resource|Icon`. The ROM maps those icon names to these sprites:

- `smile` -> `assets/icons/icon_welcome.png` -> `rom:/icon_welcome.sprite`
- `news` -> `assets/icons/icon_headlines.png` -> `rom:/icon_headlines.sprite`
- `web` -> `assets/icons/icon_web.png` -> `rom:/icon_web.sprite`
- `info` -> `assets/icons/icon_about.png` -> `rom:/icon_about.sprite`
- `diag` -> `assets/icons/icon_diagnostics.png` -> `rom:/icon_diagnostics.sprite`
- `folder` -> `assets/icons/icon_projects.png` -> `rom:/icon_projects.sprite`
- `clock` -> `assets/icons/icon_time.png` -> `rom:/icon_time.sprite`
- `chat` -> `assets/icons/icon_chat.png` -> `rom:/icon_chat.sprite`
- unknown/missing -> `assets/icons/icon_default.png` -> `rom:/icon_default.sprite`

Additional available icon:

- `assets/icons/icon_news.png`
  - Runtime sprite: `rom:/icon_news.sprite`
  - Purpose: alternate news/page icon for future menu metadata

## UI

- `assets/ui/selector_arrow.png` -> `rom:/selector_arrow.sprite`
  - Purpose: selected menu cursor
- `assets/ui/status_online.png` -> `rom:/status_online.sprite`
  - Purpose: green online indicator
- `assets/ui/status_offline.png` -> `rom:/status_offline.sprite`
  - Purpose: red offline indicator for future status screens
- `assets/ui/panel_bg.png` -> `rom:/panel_bg.sprite`
  - Purpose: replaceable panel fill tile
- `assets/ui/panel_border.png` -> `rom:/panel_border.sprite`
  - Purpose: replaceable panel border tile/corner reference
- `assets/ui/footer_bar.png` -> `rom:/footer_bar.sprite`
  - Purpose: replaceable footer legend bar

## Notes

The ROM keeps primitive fallback drawing if a sprite fails to load. This keeps
hardware testing safe while allowing the visual asset set to evolve.

The Makefile uses an explicit PNG list for conversion so user-provided source
files with spaces in their names can remain in the asset folder without breaking
the libdragon sprite build.
