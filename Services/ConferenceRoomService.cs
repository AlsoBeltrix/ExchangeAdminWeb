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
        return await RunAsync(ps =>
        {
            // Set-Place for room metadata
            ps.AddCommand("Set-Place")
              .AddParameter("Identity", roomEmail)
              .AddParameter("City", city)
              .AddParameter("Building", building)
              .AddParameter("Capacity", capacity)
              .AddParameter("Floor", floor)
              .AddParameter("ErrorAction", "Stop");
            Invoke(ps);

            // Set-User for additional properties
            ps.AddCommand("Set-User")
              .AddParameter("Identity", roomEmail)
              .AddParameter("City", city)
              .AddParameter("ErrorAction", "Stop");
            Invoke(ps);

            // Set-MailboxRegionalConfiguration for timezone
            ps.AddCommand("Set-MailboxRegionalConfiguration")
              .AddParameter("Identity", roomEmail)
              .AddParameter("TimeZone", timezone)
              .AddParameter("ErrorAction", "Stop");
            Invoke(ps);
        });
    }

    public async Task<PermissionResult> AddToRoomListAsync(string roomEmail, string city)
    {
        return await RunAsync(ps =>
        {
            var roomListName = $"RoomList-{city}";

            // Try to find existing room list DL
            ps.AddCommand("Get-DistributionGroup")
              .AddParameter("Identity", roomListName)
              .AddParameter("ErrorAction", "SilentlyContinue");
            var existing = InvokeOptional(ps);

            if (existing.Count == 0)
            {
                // Create new room list DL
                ps.AddCommand("New-DistributionGroup")
                  .AddParameter("Name", roomListName)
                  .AddParameter("DisplayName", $"{city} Rooms")
                  .AddParameter("RoomList")
                  .AddParameter("ErrorAction", "Stop");
                Invoke(ps);
            }

            // Add room to room list
            ps.AddCommand("Add-DistributionGroupMember")
              .AddParameter("Identity", roomListName)
              .AddParameter("Member", roomEmail)
              .AddParameter("ErrorAction", "Stop");
            Invoke(ps);
        });
    }

    public async Task<PermissionResult> SetRoomTypeAsync(string roomEmail, string roomType, string timezone)
    {
        return await RunAsync(ps =>
        {
            switch (roomType.ToLowerInvariant())
            {
                case "standard":
                    ps.AddCommand("Set-CalendarProcessing")
                      .AddParameter("Identity", roomEmail)
                      .AddParameter("AutomateProcessing", "AutoAccept")
                      .AddParameter("AllowConflicts", false)
                      .AddParameter("ErrorAction", "Stop");
                    Invoke(ps);
                    break;

                case "workspace":
                    ps.AddCommand("Set-CalendarProcessing")
                      .AddParameter("Identity", roomEmail)
                      .AddParameter("AutomateProcessing", "AutoAccept")
                      .AddParameter("AllowConflicts", true)
                      .AddParameter("ErrorAction", "Stop");
                    Invoke(ps);
                    break;

                case "restricted":
                    ps.AddCommand("Set-CalendarProcessing")
                      .AddParameter("Identity", roomEmail)
                      .AddParameter("AutomateProcessing", "AutoAccept")
                      .AddParameter("AllBookInPolicy", false)
                      .AddParameter("AllowConflicts", false)
                      .AddParameter("ErrorAction", "Stop");
                    Invoke(ps);
                    break;

                default:
                    throw new InvalidOperationException($"Unknown room type: {roomType}");
            }

            // Also set timezone on regional config
            ps.AddCommand("Set-MailboxRegionalConfiguration")
              .AddParameter("Identity", roomEmail)
              .AddParameter("TimeZone", timezone)
              .AddParameter("ErrorAction", "Stop");
            Invoke(ps);
        });
    }

    public async Task<RoomInfo?> GetRoomInfoAsync(string roomEmail)
    {
        return await RunPooledQueryAsync(ps =>
        {
            ps.AddCommand("Get-Mailbox")
              .AddParameter("Identity", roomEmail)
              .AddParameter("ErrorAction", "Stop");
            var mbxResults = Invoke(ps);
            var mbx = mbxResults.FirstOrDefault();
            if (mbx == null) return null;

            ps.AddCommand("Get-Place")
              .AddParameter("Identity", roomEmail)
              .AddParameter("ErrorAction", "Stop");
            var placeResults = Invoke(ps);
            var place = placeResults.FirstOrDefault();

            return new RoomInfo
            {
                Email = mbx.Properties["PrimarySmtpAddress"]?.Value?.ToString() ?? roomEmail,
                DisplayName = mbx.Properties["DisplayName"]?.Value?.ToString() ?? "",
                City = place?.Properties["City"]?.Value?.ToString() ?? "",
                Building = place?.Properties["Building"]?.Value?.ToString() ?? "",
                Capacity = int.TryParse(place?.Properties["Capacity"]?.Value?.ToString(), out var cap) ? cap : 0,
                Floor = place?.Properties["Floor"]?.Value?.ToString() ?? ""
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
}
