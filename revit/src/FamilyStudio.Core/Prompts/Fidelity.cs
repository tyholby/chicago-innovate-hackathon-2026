namespace FamilyStudio.Core.Prompts;

/// <summary>
/// How much geometric detail to aim for. A build target shared by planning, repair and review,
/// never a measured score.
/// </summary>
public sealed record Fidelity(int Level, string Name, string Summary)
{
    public static readonly Fidelity Concept = new(4, "Concept", "Silhouette, supports and material zones in their true shapes. 8 to 30 parts.");
    public static readonly Fidelity Refined = new(6, "Refined", "Principal components, tapers, curves, gaps and insets. 20 to 70 parts.");
    public static readonly Fidelity[] All = { Concept, Refined };

    public override string ToString() => Name;

    public string Instructions()
    {
        var detail = Level >= 6
            ? """
              Resolve the principal components, their proportions and orientation, visible gaps and insets,
              layered construction and distinct material zones. Match the reference's recognizable features
              with the shape that fits each one: tapered or splayed cylinders, rounded boxes for upholstery
              with a realistic crown, profiles for sculpted outlines, tubes for bent frames. Keep cushions at
              their observed thickness, separation and pose; never exaggerate them. Aim for 20 to 70 purposeful
              parts (fewer for simple items, never more than 120 counting mirror copies). More parts do not by
              themselves improve fidelity; avoid hidden or redundant pieces. A missing, flattened,
              misproportioned or misoriented principal component is a major silhouette problem even when the
              overall size is within tolerance.
              """
            : """
              Represent the dominant silhouette, the principal supports, the working surfaces and the material
              zones in their true shapes: round members round, soft parts rounded, curves curved. Aim for 8 to
              30 purposeful parts (fewer for simple items, never more than 120 counting mirror copies).
              Simplified secondary details are fine as long as the item stays recognizable.
              """;
        return $"""

            BUILD FIDELITY: {Level}/10 ({Name}). This target applies to geometry, repairs and review.
            It overrides any fidelity wording in older text, but never changes accepted dimensions,
            quantities or materials.
            {detail}
            Compare component proportions with the reference qualitatively; never measure them from pixels.
            Cushions may lie flat, recline or stand upright: follow the reference, whatever the target.
            Check front and back orientation, open space and material separation, not only overall size.
            Blocky boxes standing in for round, tapered, curved or soft components are silhouette problems
            at every target. Stitching, wood grain and photorealism are outside every target.

            """;
    }
}
