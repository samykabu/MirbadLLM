namespace MirbadLLM.Web;

/// <summary>
/// How long the "Apply changes now?" dialog waits for Docker to accept a restart before it stops waiting and
/// says so (<see cref="Components.Ui.ApplyChangesDialog.DefaultRestartWait"/> when not registered). Registered
/// only by tests that need a short bound.
/// </summary>
public sealed record ApplyRestartOptions(TimeSpan Wait);
