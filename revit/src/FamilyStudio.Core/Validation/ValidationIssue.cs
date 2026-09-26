namespace FamilyStudio.Core.Validation;

/// <summary>
/// One specific, machine-readable reason a candidate was rejected. Issues go back to the model
/// verbatim, so a correction can fix exactly the fields that failed instead of starting over.
/// </summary>
public sealed record ValidationIssue(
    string Code,
    string? AssetId,
    string? PlacementKey,
    string? PartName,
    string Property,
    object? Expected,
    object? Actual,
    double? ToleranceM,
    string Message);

public sealed class StudioValidationException : ArgumentException
{
    public IReadOnlyList<ValidationIssue> Issues { get; }

    public StudioValidationException(IEnumerable<ValidationIssue> issues)
        : this(issues.ToArray()) { }

    private StudioValidationException(ValidationIssue[] issues)
        : base(string.Join(" ", issues.Select(i => $"{i.Code} ({i.PlacementKey ?? i.PartName ?? i.AssetId}): {i.Message}")))
        => Issues = issues;

    public static StudioValidationException Single(string code, string property, string message,
        string? assetId = null, string? placementKey = null, string? partName = null,
        object? expected = null, object? actual = null, double? toleranceM = null) =>
        new(new[] { new ValidationIssue(code, assetId, placementKey, partName, property, expected, actual, toleranceM, message) });
}
