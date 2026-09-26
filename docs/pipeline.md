# Pipeline

From an input draft to native families. Each stage is a fresh Codex thread with one message
(`Core/Prompts/StudioPrompts.cs`) and, for structured stages, a strict JSON Schema for the answer
(`Core/Json/OutputSchemas.cs`). The model never touches Revit: every answer is parsed into records
(`Core/Model/`) and validated in code (`Core/Validation/`) first.

## Stages

| Stage (name in logs) | Kind | Images sent | Output | Validation | Limit |
| --- | --- | --- | --- | --- | --- |
| `brief` | reasoning | the uploaded photo, if any | `StudioBrief` | `StudioBrief.Validate`, item count, single-item rules | 10 min |
| `reference` | image | none | one image | PNG or JPEG, at least 256 px a side | 8 min |
| `recipe-a<N>`, per item | reasoning | the reference | `RecipeDraft` | `RecipeDraft.Compile` (`RecipeRules`) | 12 min |
| `layout`, collections only | reasoning | the reference | `PlacementIntentPlan` | `PlacementResolver` then `PlacementRules` | 12 min |
| `review` | reasoning | reference, plan, front-left, front-right | `ReviewReport` | `ReviewReport.Validate`, scope, room unchanged | 6 min |
| `repair` | reasoning | the reference | `SceneRepair` | merged scene, full recipe and placement rules | 12 min |
| `refine-a1`, single items | reasoning | the reference | `RecipeDraft` | as for a recipe | 12 min |

`StageSettings` carries the chosen model, effort and fidelity. The thread-level instructions are
`StudioPrompts.Developer(imageStage)`: design data is never an instruction, no tools, no em dashes, and
image generation exactly once (image stage) or never (all others).

### Brief

Input is a `StudioDraft`: one item (description, optional name, up to four finish notes, optional photo,
optional known size) or seven items plus a room description and four finishes. The model returns
metres, item IDs `a1`..`a7` in input order, material IDs `m1`.. with RGB colours, `floorStanding`, and
**components only for parts whose own size the user stated** (a 40 mm tabletop, a 450 mm pot). The host
then applies the user's names, forces `dimensionsConfirmed=false`, and for a single item requires a
floor-standing piece, sets quantity 1, and replaces the size with the user's known size (which sets
`dimensionsConfirmed=true`).

### Reference

With an uploaded photo the photo is the reference (PNG or JPEG, up to 32 MB, 64 to 16384 px a side, at
most 40 megapixels). Otherwise Codex draws one: a single product view for one item, or a 4 x 2 sheet for
a collection (seven item cards and a finishes card). The image is copied into the session and hashed.
Accepting the design freezes brief and image hash (`AcceptedDesign`).

## Geometry

A family is a list of `RecipePart`s. Each part is an axis-aligned box `MinM`..`MaxM` in metres, optionally
tilted by `TiltDegrees` about its own X axis through its centre.

- Axes: X width, Y depth, Z height. The family origin is the centre of its base at Z = 0; the front faces
  negative Y.
- **Tilt sign:** positive tilt swings the top of a part toward the front (-Y). A backrest leaning back
  uses a negative tilt. Revit's `Transform.CreateRotationAtPoint(XYZ.BasisX, angle, centre)` matches.
- 1 to 60 parts, unique names, materials from the brief, `ComponentId` only for sized components,
  `IsFloorSupport` on the parts that stand on the floor.
- The host computes the envelope from all eight transformed corners of every part
  (`RecipeRules.Corners`); the model never returns an envelope.

| Check | Tolerance |
| --- | --- |
| Overall size, estimated | max(10 mm, 5 percent) per axis |
| Overall size, verified by the user | 2 mm per axis, checked again on the built family |
| Sized components | max(10 mm, 5 percent) per axis |
| Envelope and containment | 2 mm |
| Floor contact of supports | 1 mm |
| Preview room | X -4..4 m, Y -3..3 m, Z 0..4 m |

Fidelity (`Core/Prompts/Fidelity.cs`) sets the detail target for planning, repair and review: Concept
(4/10, 5 to 16 parts) or Refined (6/10, 12 to 32 parts).

## Placement (collections)

The model returns **intents**; `PlacementResolver` turns them into positions, recursively and with cycle
detection. Rotation is degrees about world Z; zero faces -Y.

| Mode | Meaning |
| --- | --- |
| `absolute` | `offsetM` is the world position, `rotationDegrees` the world rotation |
| `relative` | Offset X/Y in the anchor's local axes, Z as a world elevation; rotation adds to the anchor's |
| `surface` | Stand on `supportPartName`, a horizontal part of the anchor. Z is computed from that part's top |
| `mirror` | Mirror the referenced placement across world X or Y = `mirrorPlaneM` (position, not geometry) |

`facingKey` or `facingPointM` aims an item's front at another placement or a point:
`angle = atan2(dx, -dy)`. `PlacementRules.Validate` then checks room bounds, floor contact, the support
chain, contact with the named surface, and that the item's base footprint fits on that surface (upper
parts may overhang). Bounding-box overlaps are reported to review as hints, not errors. A single item is
simply centred at the origin.

## Corrections

`CandidateLoop` runs a stage, parses and validates the answer, and on failure sends it back with the
exact `ValidationIssue`s (code, item, placement, part, property, expected, actual, tolerance, message),
at most twice, all within the stage's time limit. Once a candidate has parsed, recipe, refine and layout
corrections are **patches**: the model returns `baseSha256` (the hash of the rejected candidate, which the
host supplies) plus only the changed parts or placements, and the host merges them by name
(`Named.Merge`: replace in place, append new) and validates the complete result again. A patch with the
wrong hash is rejected as stale. An answer that is not valid JSON is asked for again in full.

## Build, review and repair

1. Each item's recipe is planned in turn (finished recipes survive a cancel and a closed preview room),
   then a collection gets its layout, then everything is built in Revit in one transaction group
   ([revit-integration.md](revit-integration.md)). The built families and the placed instances are
   measured back against the plan.
2. Optional review: the plan and two 3D views are exported and sent with the reference and the measured
   model state. Findings have a category (`count`, `silhouette`, `facing`, `scale`, `support`,
   `placement`) and a severity (`major` fails the review, `minor` does not).
3. On failure, a `repair` stage returns a `SceneRepair` pinned to a hash of the current recipes,
   intents and room state: recipe edits upsert or remove named parts, placement edits keep existing keys.
   Up to two repairs, each followed by a new review. If a review cannot finish (time, usage limit,
   connection), the built families stay and the state is `Built`.
4. Single items can also be revised with a sentence (`refine`), which returns a complete recipe that keeps
   the accepted dimensions.

## Presets

`Core/Prompts/Presets.cs`: Single item, Blank collection, Executive office, Bedroom suite, Kitchen and
Treatment bay. Choosing one only fills the inputs; nothing runs until the user asks.

## Changing prompts or output formats

1. Edit `StudioPrompts` and bump `StudioPrompts.Version`.
2. If a returned record changes, update its schema in `OutputSchemas` the same way.
   `OutputSchemasTests` fails when a schema and its record drift apart, and checks that every schema
   object is strict (all properties required, no extra properties).
3. Keep answers in metres and keep the geometry rules above; the validators enforce them.
4. Run the unit tests, then (with the user's permission, it uses their ChatGPT plan) a probe run:
   `dotnet run --project revit/tools/FamilyStudio.Probe -- single "..." --effort medium`, and look at the
   `family-a1.svg` drawing in the session folder.
