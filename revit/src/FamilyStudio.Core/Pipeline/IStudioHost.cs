using FamilyStudio.Core.Model;

namespace FamilyStudio.Core.Pipeline;

/// <summary>
/// Everything the pipeline needs from Revit. The implementation queues each call onto Revit's
/// API thread; no call ever holds a transaction open while waiting on AI.
/// </summary>
public interface IStudioHost : IDisposable
{
    /// <summary>Raised when the preview room document closes.</summary>
    event Action? DocumentUnavailable;

    /// <summary>Completes when no native work is queued or running.</summary>
    Task WhenIdle { get; }

    /// <summary>Binds an accepted brief. The next build starts a fresh preview room for it.</summary>
    void BindDesign(StudioBrief brief);

    Task<NativeSnapshot> EnsureRoomAsync(CancellationToken cancellationToken);

    /// <summary>Builds or rebuilds the proposal's families and places or moves its instances.</summary>
    Task<NativeSnapshot> ApplyAsync(BuildProposal proposal, NativeExpectation? expected, CancellationToken cancellationToken);

    /// <summary>Exports the plan and two 3D views of the room as images.</summary>
    Task<NativeSnapshot> CaptureAsync(CancellationToken cancellationToken);

    Task<ProjectChoice[]> ListProjectsAsync(CancellationToken cancellationToken);

    Task LoadFamiliesAsync(string projectKey, IReadOnlyList<string> assetIds, CancellationToken cancellationToken);

    /// <summary>Brings the preview room to the front in Revit.</summary>
    Task ShowRoomAsync(CancellationToken cancellationToken);

    Task CloseAsync();
}
