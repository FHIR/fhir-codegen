# Static Documentation Design Contract

Use the bundled starter to give every generated site a coherent,
accessible appearance. It is a starting layout, not a source of facts.
Do not carry its example claims or template markers into a deliverable.

## Layout and Typography

- Keep the editorial briefing structure: left navigation rail on wide
  screens, compact navigation above the content on narrow screens, a
  restrained top bar, a strong hero, and readable content cards.
- Use a local system font stack, generous line height, and a prose width
  near 70 characters. No downloaded font or icon package is required.
- The light canvas is `#f5f6f2`, with white cards, ink `#192e32`, muted
  text `#5f7073`, fine borders, and 18-pixel card corners. The green
  hero is `#123536`; accents are `#176b5c` with soft sage surfaces.
- The dark canvas is `#101b1c`, with cards `#19292b`, text `#e8f0eb`,
  muted text `#b3c3c1`, and lighter accents. Preserve hierarchy through
  surfaces and borders, not by simply inverting the light page.
- Let the content determine the layout. Use metrics only for supported
  counts, charts only when they answer a question, and cards only when
  grouping helps. A short answer does not need a dashboard.

## Palettes

Set `data-palette` on the document root to the selected preset:
`green`, `blue`, `purple`, `amber`, or `rose`. Green is the default.
The stylesheet defines coordinated light and dark accents, subtle
surfaces, and hero colors for each. Keep neutrals and semantic warnings
consistent; a blue palette does not make warnings blue.

Do not accept arbitrary CSS through this parameter. A new preset is a
separate design change that must establish contrast in both themes.
Do not silently replace an unknown palette with green.

## Theme Selection

Keep the native, labelled `theme-choice` selector with the options
`system`, `light`, and `dark`. The script uses `data-theme` to reflect
the preference; CSS resolves `system` with `prefers-color-scheme`.
Selecting system again relinquishes an earlier explicit override.

Replace the root's `data-theme-key` placeholder with a stable, unique
site identifier and use it on every page. Distinct reports served from
the same origin must not unintentionally share preferences. Keep the
identifier stable on regeneration. It is not a secret.

Load `theme.js` synchronously in the head **before** `theme.css`, as in
the starter. It reads and applies the preference before first styling,
then wires the selector when the document is ready. Do not add `defer`,
move it below content, or duplicate the initialization in every page.
The CSS supplies system-based dark mode even with scripting disabled.

Catch only storage operations that may be unavailable. A denied storage
read or write must produce a visible status and a console warning, while
the selector continues to apply the choice for the current page. Do not
disable reading because persistence failed. Browser storage under
`file:` may be isolated per file; cross-page persistence is guaranteed
only when the browser provides shared storage, such as static HTTP.

## Static Delivery and Content Safety

- Copy `theme.css` and `theme.js` to the output root. Root pages use
  those filenames; nested pages use the correct relative URL depth.
  Navigation links are ordinary HTML files or fragment links.
- Do not require a build system, route rewrite, backend, remote font,
  CDN, analytics, runtime data fetch, or client-side Markdown renderer.
  Opening `index.html` must expose the complete content immediately.
- Embed source excerpts as escaped text, including quotes inside
  attributes. Never paste arbitrary source markup into the document.
  Validate source URLs; allow ordinary relative references and `https:`
  or `http:` evidence links, never executable URL schemes.
- Prefer same-tab links. If a link opens a new tab, include
  `rel="noopener noreferrer"`. Keep the no-referrer metadata.
- No application logic belongs in the output. The bundled theme script
  is enough by default. Add an optional local enhancement only when the
  documented reader need justifies it and the no-script view is complete.
- Include the scope, source revision or retrieval dates, citations,
  unresolved questions, and evidence limitations in the shareable pages.
  Do not link to `request.md` or other private control artifacts.

## Accessibility and Responsive Behavior

Keep semantic landmarks, one page-level heading, ordered heading levels,
the skip link, and `aria-current="page"` on a multi-page site's current
navigation link. Keep visible keyboard focus. Label every form control.

Normal text must reach a contrast ratio of at least **4.5:1**; large text
and meaningful non-text controls must reach **3:1**. Check both themes
and the selected palette, including hover and focus states. Never use
color alone to communicate a finding or a chart category.

At narrow widths, stack content cards and expose navigation without
requiring a script-controlled menu. Wrap long source paths and titles.
Put genuinely wide tables and code in labelled scroll regions that can
be reached by keyboard; do not let them widen the whole page.

Honor reduced motion. In print, use light backgrounds and dark text,
hide navigation and theme controls, retain citations and limitations,
and avoid clipping tables, code, or essential content.

## Verification Matrix

Inspect every generated page for the shared shell and local links, and
exercise every distinct component with an available browser:

| Case | Expected result |
|-|-|
| Fresh visit, system light or dark | Follows the system without a saved choice |
| Explicit light or dark | Overrides the system; selector reports that choice |
| Return to system | Tracks later system changes without a reload |
| Reload or another page | Retains the choice when shared storage is available |
| Another tab changes the choice | Reflects the matching site's storage event |
| Storage unavailable | Selection still works; failure is visible |
| Scripting disabled | Content and navigation work; follows system theme |
| Narrow viewport and zoom | No page-wide overflow; controls and text stay usable |
| Keyboard only | Skip link, links, theme choice, and scroll regions work |
| Print from either theme | Readable light page; no clipped essential content |
| Static host below a URL subpath | Pages, assets, and fragments resolve |

Do not install browser tooling or invent preview commands. Record the
checks actually performed and those unavailable. Static inspection is
not evidence that a browser scenario passed.
