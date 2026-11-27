
namespace Reqnroll.IdeSupport.Common.Telemetry;

/// <summary>
/// Supplies the anonymous installation id sent with telemetry (Application Insights <c>ai.user.id</c>).
/// </summary>
/// <remarks>
/// The id is a random GUID generated once per installation of the extension on a machine. It does not
/// identify a person: it only lets events from one installation be grouped, to approximate the number of
/// installations and follow upgrades. Callers must not ask for it before telemetry is known to be
/// permitted, because the first call may create and persist it.
/// </remarks>
public interface IInstallationIdStore
{
    /// <summary>Returns the persisted installation id, creating and persisting one if none exists yet.</summary>
    string GetInstallationId();
}
