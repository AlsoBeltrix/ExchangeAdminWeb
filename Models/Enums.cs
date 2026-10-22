namespace ExchangeAdminWeb.Models;

public enum PermissionType { FullAccess, SendAs }

public enum PermissionAction { Add, Remove }

public enum CalendarAccessRight
{
    None,
    Owner,
    PublishingEditor,
    Editor,
    PublishingAuthor,
    Author,
    NonEditingAuthor,
    Reviewer,
    Contributor,
    AvailabilityOnly,
    LimitedDetails
}

public enum MigrationDirection
{
    ToCloud,
    ToOnPrem
}

public enum MigrationStatus
{
    Eligible,
    Ineligible,

    /// <summary>
    /// aud-a23h. A read the verdict needed failed, so there is none: neither eligible nor ineligible. Shown as "could not
    /// be determined", and - not being Eligible - never offered or given a batch.
    /// </summary>
    Undetermined
}
