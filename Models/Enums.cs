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
