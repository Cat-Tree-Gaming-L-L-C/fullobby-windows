# Fullobby.ArtGen

Programmatic source of truth for the brand art. Renders the icon glyph (gold M1
helmet over a full capacity meter on the dark brand tile) and the text logo
(FULLOBBY in the bundled Bebas Neue over a ten-helmet meter at 100/100), straight
from the `Brand.xaml` palette and `assets/fonts/BebasNeue-Regular.ttf`.

Overwrites in place:

- `icons/icon.ico` (16–256, PNG-compressed entries) and the full `icons/*.png` set
- `assets/logo_no_text.png` (in-app glyph: titlebar / About / onboarding)
- `assets/logo_text.png` (wordmark, marketing use)

Run from anywhere inside the repo (Windows-only — uses System.Drawing + GDI+):

```
dotnet run --project tools/artgen
```

To tweak the design, edit the proportions/palette in `Program.cs` and re-run —
don't edit the PNGs by hand.
