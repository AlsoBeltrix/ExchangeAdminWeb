using System.Management.Automation;
using ExchangeAdminWeb.Models;

namespace ExchangeAdminWeb.Services;

public class ConferenceRoomService : ExchangeServiceBase
{
    public ConferenceRoomService(
        ExoConnectionPool exoPool,
        DelineaService delineaService,
        IConfiguration config,
        ILogger<ConferenceRoomService> logger)
        : base(exoPool, delineaService, logger, config["OnPremExchange:ServerUri"] ?? "")
    {
    }

    public async Task<PermissionResult> SetRoomMetadataAsync(string roomEmail, string city, string building, int capacity, string floor, string timezone)
    {
        // Validate timezone before any Exchange mutations to avoid partial commits
        if (!string.IsNullOrWhiteSpace(timezone))
        {
            try
            {
                TimeZoneInfo.FindSystemTimeZoneById(timezone);
            }
            catch (TimeZoneNotFoundException)
            {
                return PermissionResult.Fail($"Invalid timezone: '{timezone}'. Use a valid Windows timezone ID (e.g. 'Eastern Standard Time').");
            }
        }

        return await RunAsync((ps, tracker) =>
        {
            // Set-Place for room metadata
            ps.AddCommand("Set-Place")
              .AddParameter("Identity", roomEmail)
              .AddParameter("City", city)
              .AddParameter("Building", building)
              .AddParameter("Capacity", capacity)
              .AddParameter("Floor", floor)
              .AddParameter("ErrorAction", "Stop");
            Invoke(ps, tracker);

            // Set-User for additional properties
            ps.AddCommand("Set-User")
              .AddParameter("Identity", roomEmail)
              .AddParameter("City", city)
              .AddParameter("ErrorAction", "Stop");
            Invoke(ps, tracker);

            // Set-MailboxRegionalConfiguration for timezone (skip if not provided)
            if (!string.IsNullOrWhiteSpace(timezone))
            {
                ps.AddCommand("Set-MailboxRegionalConfiguration")
                  .AddParameter("Identity", roomEmail)
                  .AddParameter("TimeZone", timezone)
                  .AddParameter("ErrorAction", "Stop");
                Invoke(ps, tracker);
            }
        });
    }

    public async Task<PermissionResult> AddToRoomListAsync(string roomEmail, string city)
    {
        return await RunAsync((ps, tracker) =>
        {
            var roomListName = $"RoomList-{city}";

            // Try to find existing room list DL
            ps.AddCommand("Get-DistributionGroup")
              .AddParameter("Identity", roomListName)
              .AddParameter("ErrorAction", "SilentlyContinue");
            var existing = InvokeOptional(ps, tracker);

            if (existing.Count == 0)
            {
                // Create new room list DL
                ps.AddCommand("New-DistributionGroup")
                  .AddParameter("Name", roomListName)
                  .AddParameter("DisplayName", $"{city} Rooms")
                  .AddParameter("RoomList")
                  .AddParameter("ErrorAction", "Stop");
                Invoke(ps, tracker);
            }

            // Add room to room list
            ps.AddCommand("Add-DistributionGroupMember")
              .AddParameter("Identity", roomListName)
              .AddParameter("Member", roomEmail)
              .AddParameter("ErrorAction", "Stop");
            Invoke(ps, tracker);
        });
    }

    public async Task<PermissionResult> SetRoomTypeAsync(string roomEmail, string roomType, string timezone)
    {
        // Validate timezone before any Exchange mutations to avoid partial commits
        if (!string.IsNullOrWhiteSpace(timezone))
        {
            try
            {
                TimeZoneInfo.FindSystemTimeZoneById(timezone);
            }
            catch (TimeZoneNotFoundException)
            {
                return PermissionResult.Fail($"Invalid timezone: '{timezone}'. Use a valid Windows timezone ID (e.g. 'Eastern Standard Time').");
            }
        }

        return await RunAsync((ps, tracker) =>
        {
            switch (roomType.ToLowerInvariant())
            {
                case "standard":
                    ps.AddCommand("Set-CalendarProcessing")
                      .AddParameter("Identity", roomEmail)
                      .AddParameter("AutomateProcessing", "AutoAccept")
                      .AddParameter("AllowConflicts", false)
                      .AddParameter("ErrorAction", "Stop");
                    Invoke(ps, tracker);
                    break;

                case "workspace":
                    ps.AddCommand("Set-CalendarProcessing")
                      .AddParameter("Identity", roomEmail)
                      .AddParameter("AutomateProcessing", "AutoAccept")
                      .AddParameter("AllowConflicts", true)
                      .AddParameter("ErrorAction", "Stop");
                    Invoke(ps, tracker);
                    break;

                case "restricted":
                    ps.AddCommand("Set-CalendarProcessing")
                      .AddParameter("Identity", roomEmail)
                      .AddParameter("AutomateProcessing", "AutoAccept")
                      .AddParameter("AllBookInPolicy", false)
                      .AddParameter("AllowConflicts", false)
                      .AddParameter("ErrorAction", "Stop");
                    Invoke(ps, tracker);
                    break;

                default:
                    throw new InvalidOperationException($"Unknown room type: {roomType}");
            }

            // Also set timezone on regional config
            ps.AddCommand("Set-MailboxRegionalConfiguration")
              .AddParameter("Identity", roomEmail)
              .AddParameter("TimeZone", timezone)
              .AddParameter("ErrorAction", "Stop");
            Invoke(ps, tracker);
        });
    }

    public async Task<RoomInfo?> GetRoomInfoAsync(string roomEmail)
    {
        return await RunPooledQueryAsync((ps, tracker) =>
        {
            ps.AddCommand("Get-Mailbox")
              .AddParameter("Identity", roomEmail)
              .AddParameter("ErrorAction", "Stop");
            var mbxResults = Invoke(ps, tracker);
            var mbx = mbxResults.FirstOrDefault();
            if (mbx == null) return null;

            ps.AddCommand("Get-Place")
              .AddParameter("Identity", roomEmail)
              .AddParameter("ErrorAction", "Stop");
            var placeResults = Invoke(ps, tracker);
            var place = placeResults.FirstOrDefault();

            // Retrieve current timezone from regional configuration (best-effort)
            string timezone = "";
            try
            {
                ps.AddCommand("Get-MailboxRegionalConfiguration")
                  .AddParameter("Identity", roomEmail)
                  .AddParameter("ErrorAction", "SilentlyContinue");
                var regionalResults = InvokeOptional(ps, tracker);
                var regional = regionalResults.FirstOrDefault();
                timezone = regional?.Properties["TimeZone"]?.Value?.ToString() ?? "";
            }
            catch { /* best-effort; do not fail the lookup */ }

            return new RoomInfo
            {
                Email = mbx.Properties["PrimarySmtpAddress"]?.Value?.ToString() ?? roomEmail,
                DisplayName = mbx.Properties["DisplayName"]?.Value?.ToString() ?? "",
                City = place?.Properties["City"]?.Value?.ToString() ?? "",
                Building = place?.Properties["Building"]?.Value?.ToString() ?? "",
                Capacity = int.TryParse(place?.Properties["Capacity"]?.Value?.ToString(), out var cap) ? cap : 0,
                Floor = place?.Properties["Floor"]?.Value?.ToString() ?? "",
                TimeZone = timezone
            };
        });
    }
}

public class RoomInfo
{
    public string Email { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string City { get; set; } = "";
    public string Building { get; set; } = "";
    public int Capacity { get; set; }
    public string Floor { get; set; } = "";
    public string TimeZone { get; set; } = "";
}
