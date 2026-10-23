namespace ExchangeAdminWeb.Services;

/// <summary>
/// The directory reads Module Configuration's grant editors depend on: whether the directory can be asked, and what
/// one group SID names - the confirmation every grant a save adds goes through (aud-a1b).
/// </summary>
/// <remarks>
/// A seam for the reason <see cref="IOperatorDirectory"/> gives: <see cref="ADDirectorySearchService"/> is sealed, and
/// unsealing a live-AD service for test convenience widens it for every other caller. Registered as that singleton, so
/// production asks the same service the same two questions it always did. aud-a66: the settings page harness hands the
/// page a stand-in, so a test can hold a lookup while the page's route moves to another module.
/// </remarks>
public interface IGroupGrantDirectory
{
    /// <summary>True when the ActiveDirectory module is loadable and the directory reachable.</summary>
    bool IsAvailable { get; }

    /// <summary>Whether a SID names an object in the forest, and what class it is.</summary>
    DirectoryValidationResult ValidateGroupSid(string sid);
}
