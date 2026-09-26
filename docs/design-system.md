# Family Studio design system

Family Studio does one thing: it turns a description or a product photo into a native furniture
family. The interface should feel like a precision drawing instrument, not a dashboard. Its
vocabulary comes from the drawing set every architect already reads: sheets, title blocks,
dimension strings, construction lines and redlines.

The system is implemented twice from the same tokens:

| Host | Where |
| --- | --- |
| Revit (WPF) | `revit/src/FamilyStudio.Revit/UI/Theme/` (`Paper.xaml`, `Night.xaml`, `Studio.xaml`) |
| Rhino (Eto) | `rhino/src/FamilyStudio.Rhino/Theme.cs` |

## Principles

1. **A sheet, not a dashboard.** Every step is laid out as a drawing sheet: a drawing area for the
   object, a schedule column for its data, and a title block along the bottom. Chrome stays at
   hairline weight so the furniture is the hero.
2. **Line weight before colour.** Hierarchy comes from weight, position and size, on the 1 : 2 : 4
   ratio of ISO 128 line weights. The active tab or focused field sits on a 2 px cut line;
   everything else is a hairline.
3. **One signal colour.** Cobalt means "act here" or "selected". It echoes Revit's own selection
   blue. Red is reserved for redlines (errors). Material colours appear only in swatches and in
   the model itself.
4. **Numbers are sacred.** Dimensions are set in monospaced figures, with units, on dimension
   strings. No sliders.
5. **Pencil, then ink.** Values the AI proposed are drawn in construction blue (non-photo blue)
   until the architect checks them; checking inks them. This shows where AI was involved without
   sparkle icons.
6. **Show the work.** Long AI steps read like a drawing issue register: what ran, when, and for how
   long. Visible effort is honest, and it builds trust.
7. **Native to the host.** The Revit window follows Revit's light or dark UI theme, and the Rhino
   panel follows Rhino's.

## Sheets

The three steps are sheets, after a cover sheet. Tabs name them in words only: no sheet numbers,
which read as codes to anyone outside a drawing office.

| Sheet | Drawing area | Schedule column |
| --- | --- | --- |
| Cover | Sign in with ChatGPT | General notes: what is sent where |
| Brief | The photo plate, or seven item cards and a finishes card | Name, general notes, overall dimensions, finishes |
| Reference | The generated reference, or your photo | Family name, dimensions, the verified stamp, finish schedule |
| Build | Native Revit views: plan, front left, front right | Detail level, families, revise, review, load into project |

The **title block** runs along the bottom of every sheet: a status note, then cells for the family
and its status, then the one primary action. The header carries the mark and wordmark only, with no
host or version line. A 1 px drafting line across its top edge
carries a travelling ink stroke while Family Studio works.

## Tokens

Two themes with identical keys. Contrast ratios are WCAG 2.x, text against the background and
against the raised surface (the lower value is the worst case).

| Token | Paper (light) | Ratio | Night drafting (dark) | Ratio |
| --- | --- | --- | --- | --- |
| `FS.Bg` background | `#F4F2ED` | | `#1C2027` | |
| `FS.Surface` | `#FAF9F6` | | `#242932` | |
| `FS.Raised` preview canvas, cards | `#FFFFFF` | | `#2E343F` | |
| `FS.Rule` hairline | `#DEDAD1` | decorative | `#3A414E` | decorative |
| `FS.RuleStrong` field baselines | `#8C877C` | 3.20 | `#808BA0` | 3.64 |
| `FS.Ink` text | `#1B1A17` | 15.55 / 17.40 | `#EEF0F3` | 14.31 / 10.95 |
| `FS.Ink2` secondary text | `#4F4B44` | 7.75 / 8.67 | `#B6BDC9` | 8.65 / 6.62 |
| `FS.Ink3` tertiary text | `#6A655B` | 5.18 / 5.79 | `#9CA4B1` | 6.50 / 4.98 |
| `FS.Accent` cobalt | `#2B45CF` | 6.57, white label 7.36 | `#8FA3FF` | 6.89, dark label 7.70 |
| `FS.AccentHover` | `#2139B3` | | `#A5B5FF` | |
| `FS.AccentPressed` | `#1A2E94` | | `#7389F2` | |
| `FS.Construction` non-photo blue, linework only | `#3D8FC6` | 3.53 | `#A4DDED` | 8.42 |
| `FS.Success` | `#2E6B3F` | 5.70 | `#74CE90` | 8.54 |
| `FS.Warning` | `#7A4D00` | 6.50 | `#E8B754` | 8.82 |
| `FS.Danger` redline, errors only | `#B8321F` | 5.34 | `#FF8472` | 6.84 |
| `FS.Selection` | `#DDE3FA` | ink 13.63 | `#2C3866` | ink 9.86 |

Always use them as `DynamicResource` in WPF, so the theme switches live when Revit's does.

**Spacing** is a 4 px module: 2, 4, 8, 12, 16, 24, 32, 48. Controls are 32 px high (36 for the
primary action); the schedule column is 372 px.

**Radius** is 0 for sheets, images and plates, 2 for controls and 4 for popovers. The only circle
in the system is the grid reference bubble.

**Lines** are a 1 px hairline, a 2 px cut line for what is active, and dashed construction lines
for empty plates. Shadows appear only on popovers.

## Type

All faces ship with Windows, so nothing is embedded or licensed.

| Role | Face | Size | Notes |
| --- | --- | --- | --- |
| Display (cover) | Segoe UI Variable Display, Light | 30 to 40 | The only large type |
| Title | Segoe UI Variable Display, SemiBold | 20 | Family name, step titles |
| Heading | Segoe UI Variable Text, SemiBold | 15 | |
| Body | Segoe UI Variable Text | 13 / 19 | Secondary ink |
| Small, help | Segoe UI Variable Text | 12 / 16 | Tertiary ink |
| Label, tracked caps | Bahnschrift SemiCondensed, SemiBold | 11 | DIN 1451, the lettering of technical drawings. Title block, sheet tabs, section labels |
| Figures | Cascadia Mono, then Consolas | 10 to 15 | Dimensions, scales, times, hex values |

WPF has no letter-spacing, so tracked capitals (`ui:Caps` in Revit, `Type.Caps` in Rhino) insert a
hair space between letters: about 8 percent tracking at label sizes. Tracked capitals are never used
on buttons or body text.

## Components

| Component | Key | Spec |
| --- | --- | --- |
| Sheet tab | `FS.SheetTab` | The sheet's name in tracked capitals. Active: ink on a 2 px cut line |
| Plate | `FS.Plate` | A figure framed by crop marks. Empty: a dashed construction border and one clear call to action |
| Field | `FS.Field` | A baseline, not a box. Focus draws a 2 px cobalt cut line |
| General notes | `FS.Notes` | A multi-line field on ruled lines, 22 px apart |
| Dimension string | `FS.Dimension` | The value above a dimension line closed by 45 degree ticks. Construction blue until checked, ink after |
| Verified stamp | `FS.Stamp` | "Dimensions not verified" in ochre with a dashed border, or "Dimensions verified" in green, solid |
| Segments | `FS.Segments` + `FS.Segment` | Units, detail level, views. The chosen one is filled with ink |
| Primary button | `FS.Button.Primary` | Cobalt, one per sheet, in the title block's last cell, verb first ("Build family") |
| Secondary button | `FS.Button.Secondary` | Hairline outline |
| Quiet button | `FS.Button.Quiet` | Cobalt text, underline on hover |
| Grid bubble | `FS.Bubble` | A circle with a two-digit number, for collection items |
| Finish chip | (template) | Swatch, `MT-01` code in figures, name, hex value |
| Status stamp | (title block) | Preliminary, For review, Checked, Built, Issued, or Not issued in redline |
| Drafting line | `ui:DraftingLine` | A hairline with a travelling ink stroke while work is in progress |
| Issue register | (popup) | Time, event and duration of every step, errors in redline |

## Motion and states

- 100 ms for press, 150 ms for hover, 200 ms for a step change. Linear progress, no springs, and no
  animation at all when Windows animations are turned off.
- **Long AI steps** (30 seconds to several minutes): the drafting line moves, the status note names
  the step ("Designing family 3 of 7: Guest chair..."), and the issue register records it. The
  window stays modeless, and Cancel always keeps the last good result.
- **Empty**: a plate with crop marks and one instruction, never an illustration.
- **Errors**: a redline note in the title block with the fix, never a modal dialog.

## Influences

Arcol and Motif for warm paper and a single accent; Rayon for construction-line texture; Figma's UI3
for keeping panels fixed and the canvas first; Shapr3D for keeping modes in fixed places; Autodesk's
own Weave tokens for sitting comfortably inside Revit; ISO 128 line weights and
drafting conventions (dimension ticks, non-photo blue, redlines, title blocks) for the vocabulary;
Dieter Rams and the Swiss grid for restraint.
