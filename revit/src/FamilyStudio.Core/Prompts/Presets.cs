using FamilyStudio.Core.Model;

namespace FamilyStudio.Core.Prompts;

public sealed record Preset(string Id, string Name, string Caption, StudioDraft Draft)
{
    public override string ToString() => Name;
}

/// <summary>Starting points. Every field stays editable; choosing a preset never runs anything by itself.</summary>
public static class Presets
{
    public const string SingleId = "single";

    public static IReadOnlyList<Preset> All { get; } = new[]
    {
        new Preset(SingleId, "Single item", "One piece from words or a photo", StudioDraft.EmptySingleItem()),
        new Preset("blank", "Blank collection", "Seven items, four materials", StudioDraft.EmptyCollection()),
        new Preset("office", "Executive office", "Desk, seating, storage, lamp, rug, planter", new StudioDraft(
            "A calm executive office. Warm walnut, charcoal leather, brushed bronze and ivory. Architectural, " +
            "restrained and cohesive. The desk anchors the room with two guest chairs facing it and a credenza behind.",
            new[]
            {
                "Executive desk in dark walnut with slab end panels and a recessed modesty panel. 2400 x 1000 x 750 mm.",
                "Executive chair with charcoal leather upholstery, a five-star base and a high back. 700 x 720 x 1200 mm.",
                "Guest chair, two matching instances facing the desk. Charcoal leather seat on a slim bronze frame. 620 x 660 x 830 mm each.",
                "Compact brushed bronze task lamp, 480 mm high, standing on the desk.",
                "Area rug in warm ivory wool, 3400 x 4600 mm and 12 mm thick, under the desk and guest chairs.",
                "Low walnut credenza with four doors and bronze pulls. 2200 x 450 x 720 mm.",
                "Planter, two instances: a sculptural plant 1600 mm tall overall in a 450 mm square pot. Foliage may spread beyond the pot."
            },
            new[] { "Dark walnut for the desk and credenza", "Charcoal leather for both chairs", "Brushed bronze for the lamp and accents", "Ivory wool rug and green foliage" },
            new[] { "Executive desk", "Executive chair", "Guest chair", "Desk lamp", "Area rug", "Credenza", "Planter" })),
        new Preset("bedroom", "Bedroom suite", "Bed, bedside pair, storage, bench, lamps, rug", new StudioDraft(
            "A quiet, generous bedroom suite in warm walnut, ivory upholstery, muted sage textiles and brushed brass. " +
            "The king bed is the focus, flanked by matching bedside tables with lamps, a bench at its foot, a rug beneath, " +
            "and a dresser and wardrobe along the side walls.",
            new[]
            {
                "King bed, one instance. 2200 x 2300 x 1200 mm including the headboard. Walnut plinth frame, thick ivory mattress and an upholstered headboard at the back (+Y); the foot faces -Y.",
                "Bedside table, two identical instances either side of the bed. 600 x 450 x 600 mm each, walnut with two drawers and brass pulls, standing on the floor.",
                "Dresser, one instance. 1600 x 450 x 850 mm low walnut chest of six drawers with brass pulls on a full plinth.",
                "Wardrobe, one instance. 1800 x 600 x 2200 mm freestanding walnut double-door wardrobe with clear door reveals, brass pulls and a plinth.",
                "Bench, one instance at the foot of the bed. 1400 x 450 x 450 mm with an ivory upholstered cushion on four square walnut legs.",
                "Bedside lamp, two identical instances, one on each bedside table. 300 x 300 x 450 mm: a square brass base, a slim stem and an ivory fabric shade.",
                "Area rug, one instance under the bed and bench. 3400 x 4300 x 12 mm in sage wool."
            },
            new[] { "Warm walnut for frames, carcasses, doors and legs", "Ivory upholstery for the headboard, mattress, bench and lamp shades",
                "Muted sage wool for the rug and bedding accents", "Brushed brass for pulls, lamp bases and stems" },
            new[] { "King bed", "Bedside table", "Dresser", "Wardrobe", "Bench", "Bedside lamp", "Area rug" })),
        new Preset("kitchen", "Kitchen", "Island, cabinetry, appliances, stools", new StudioDraft(
            "A contemporary kitchen in warm white cabinetry, light oak, pale stone and brushed steel. An island with stools " +
            "faces a rear run of base cabinets, appliances and wall cabinets.",
            new[]
            {
                "Kitchen island with closed storage and a pale stone worktop, with a seating overhang on the stool side. 2400 x 1100 x 920 mm.",
                "Base cabinet with three drawers and a stone worktop. Two matching instances beside the appliances. 800 x 600 x 920 mm each.",
                "Wall cabinet with two doors. Two matching instances mounted above the base cabinets, not floor-standing. 800 x 350 x 700 mm each.",
                "Freestanding refrigerator with two tall doors over a freezer drawer. 900 x 700 x 1850 mm.",
                "Range cooker with an oven front, control panel and cooktop. 900 x 650 x 920 mm.",
                "Sink unit with cabinet doors, a stone worktop, an inset basin and a simple tap. 1000 x 600 x 920 mm.",
                "Counter stool with a low back, an upholstered seat and a footrest. Three matching instances at the island. 450 x 480 x 950 mm each."
            },
            new[] { "Warm white cabinet fronts", "Light natural oak accents and stool frames", "Pale honed stone worktops", "Brushed stainless steel appliances and fittings" },
            new[] { "Island", "Base cabinet", "Wall cabinet", "Refrigerator", "Range cooker", "Sink unit", "Counter stool" })),
        new Preset("treatment-bay", "Treatment bay", "Clinical equipment and seating", new StudioDraft(
            "A contemporary emergency treatment bay, as an architectural concept collection. Off-white equipment housings, " +
            "brushed stainless steel, muted blue upholstery and charcoal details. Mobile equipment surrounds the stretcher " +
            "with a clear approach, and visitor seating sits to one side.",
            new[]
            {
                "Patient stretcher with a mattress, raised side rails and a caster base, with a clear head end and foot end. 900 x 2100 x 900 mm.",
                "Mobile monitor cart with a screen, an equipment shelf and a caster base, beside the stretcher. 600 x 550 x 1500 mm.",
                "Mobile supply trolley with drawers, a work surface and casters, reachable from the side of the stretcher. 700 x 500 x 950 mm.",
                "Tall enclosed supply cabinet with double doors and recessed handles, against the rear wall. 1000 x 500 x 2000 mm.",
                "Clinician stool with an upholstered seat, a short back and a caster base. 550 x 550 x 850 mm.",
                "Visitor chair with arms, an upholstered seat and a wipe-clean frame. 600 x 620 x 820 mm.",
                "Freestanding examination light on a weighted base with an adjustable arm and a compact head, beside the stretcher. 600 x 600 x 1800 mm."
            },
            new[] { "Off-white equipment housings and cabinet fronts", "Brushed stainless steel frames and work surfaces",
                "Muted blue vinyl upholstery and mattress covers", "Charcoal controls, casters and screen bezels" },
            new[] { "Patient stretcher", "Monitor cart", "Supply trolley", "Supply cabinet", "Clinician stool", "Visitor chair", "Examination light" }))
    };

    /// <summary>A deep copy, so editing a draft never mutates the preset.</summary>
    public static StudioDraft Create(string id)
    {
        var preset = All.FirstOrDefault(p => p.Id == id) ?? All[0];
        var d = preset.Draft;
        return new(d.Style, d.Assets.ToArray(), d.Materials.ToArray(), d.AssetNames.ToArray());
    }
}
