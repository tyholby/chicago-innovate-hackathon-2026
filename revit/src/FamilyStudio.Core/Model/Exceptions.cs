namespace FamilyStudio.Core.Model;

/// <summary>The ChatGPT plan's usage limit was reached. Carries the reset time when Codex reports one.</summary>
public sealed class StudioLimitException(string message, DateTimeOffset? resetsAt) : Exception(message)
{
    public DateTimeOffset? ResetsAt { get; } = resetsAt;
}

/// <summary>Codex did something this plugin does not allow (an unexpected tool, a substituted model, a missing image).</summary>
public sealed class StudioProtocolException(string message) : Exception(message);

/// <summary>The bound Revit document closed or is otherwise unavailable.</summary>
public sealed class StudioDocumentException(string message) : Exception(message);

/// <summary>The preview room changed under an operation, so its evidence no longer describes the model.</summary>
public sealed class StudioEvidenceException(string message) : Exception(message);

/// <summary>The user is not signed in with ChatGPT, or the sign-in expired.</summary>
public sealed class StudioSignInRequiredException(string message) : Exception(message);
