using System.Security.Claims;
using System.Text.Json;

namespace ExchangeAdminWeb.Services;

public class M365GroupManagementService
{
    private readonly ILogger<M365GroupManagementService> _logger;
    private readonly ModuleConfigService _moduleConfig;
    private readonly DelineaService _delineaService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly OperationTraceService _operationTrace;
    private readonly AuditService _audit;
    private readonly ProtectedPrincipalService _protectedPrincipals;
    private readonly ProtectedPrincipalServicerService _servicers;

    /// <summary>Module id for the servicer grant. Must match the catalog descriptor.</summary>
    private const string ServicerModuleId = "M365GroupManagement";

    public M365GroupManagementService(
        ModuleConfigService moduleConfig,
        DelineaService delineaService,
        IHttpClientFactory httpClientFactory,
        OperationTraceService operationTrace,
        AuditService audit,
        ProtectedPrincipalService protectedPrincipals,
        ProtectedPrincipalServicerService servicers,
        ILogger<M365GroupManagementService> logger)
    {
        _moduleConfig = moduleConfig;
        _delineaService = delineaService;
        _httpClientFactory = httpClientFactory;
        _operationTrace = operationTrace;
        _audit = audit;
        _protectedPrincipals = protectedPrincipals;
        _servicers = servicers;
        _logger = logger;
    }

    /// <summary>Test seam: the Graph client the service uses, in place of one built from its secret.</summary>
    internal Func<GraphTokenClient>? GraphClientForTests { get; set; }

    private async Task<GraphTokenClient> GetGraphClientAsync()
    {
        if (GraphClientForTests is { } forTests)
            return forTests();

        var secretIdStr = _moduleConfig.GetValue("M365GroupManagement", "GraphDelineaSecretId");
        if (!int.TryParse(secretIdStr, out var secretId) || secretId <= 0)
            throw new InvalidOperationException("M365 Group Management module is not configured. Set Graph App Delinea Secret ID in Module Config.");

        var fields = await _delineaService.GetSecretFieldsAsync(secretId);
        if (fields == null)
            throw new InvalidOperationException($"Cannot retrieve M365 Group Management Graph app secret {secretId} from Secret Server. Verify this is the correct Secret ID and that the Delinea SDK client can view it.");

        var tenantId = fields.GetValueOrDefault("Tenant ID") ?? "";
        var clientId = fields.GetValueOrDefault("Application ID") ?? "";
        var clientSecret = fields.GetValueOrDefault("Client Secret") ?? "";

        if (string.IsNullOrEmpty(tenantId) || string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret))
            throw new InvalidOperationException("Graph API credentials incomplete in Secret Server.");

        return new GraphTokenClient(tenantId, clientId, clientSecret, _httpClientFactory.CreateClient("MicrosoftGraph"));
    }

    public bool IsAvailable
    {
        get
        {
            var secretIdStr = _moduleConfig.GetValue("M365GroupManagement", "GraphDelineaSecretId");
            return int.TryParse(secretIdStr, out var id) && id > 0;
        }
    }

    public virtual async Task<List<M365Group>> SearchGroupsAsync(string searchTerm)
    {
        var client = await GetGraphClientAsync();
        var groups = new List<M365Group>();

        var odataEscaped = searchTerm.Replace("'", "''");
        var filterQuery = Uri.EscapeDataString($"groupTypes/any(g:g eq 'Unified') and startsWith(displayName,'{odataEscaped}')");
        var endpoint = $"/groups?$filter={filterQuery}&$select=id,displayName,mail,description,visibility,createdDateTime&$top=50";

        var (doc, status) = await client.GetWithStatusAsync(endpoint);
        if (doc == null)
        {
            // A failed search must not render as "no groups found".
            throw new InvalidOperationException($"Graph search for M365 groups failed: {(int)status} {status}.");
        }
        using var _ = doc;

        foreach (var item in doc.RootElement.GetProperty("value").EnumerateArray())
        {
            groups.Add(ParseGroup(item));
        }

        return groups.OrderBy(g => g.DisplayName).ToList();
    }

    public virtual async Task<M365Group?> GetGroupDetailsAsync(string groupId)
    {
        var client = await GetGraphClientAsync();
        using var doc = await client.GetAsync($"/groups/{Uri.EscapeDataString(groupId)}?$select=id,displayName,mail,description,visibility,groupTypes,mailEnabled,securityEnabled,createdDateTime");
        if (doc == null) return null;
        return ParseGroup(doc.RootElement);
    }

    public async Task<M365GroupResult> CreateGroupAsync(string displayName, string mailNickname, string? description, string visibility)
    {
        var client = await GetGraphClientAsync();
        var body = new Dictionary<string, object>
        {
            ["displayName"] = displayName,
            ["mailNickname"] = mailNickname,
            ["description"] = description ?? "",
            ["visibility"] = visibility,
            ["groupTypes"] = new[] { "Unified" },
            ["mailEnabled"] = true,
            ["securityEnabled"] = false
        };

        using var doc = await client.PostAsync("/groups", body);
        if (doc == null)
            return new M365GroupResult { Success = false, Message = "Failed to create M365 group. Check module configuration and Graph app permissions (Group.ReadWrite.All)." };

        var id = doc.RootElement.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
        _logger.LogInformation("Created M365 group '{Name}' (id={Id})", displayName, id);
        return new M365GroupResult { Success = true, Message = $"Created M365 group '{displayName}'." };
    }

    public async Task<M365GroupResult> UpdateGroupAsync(string groupId, string displayName, string? description, string visibility)
    {
        var client = await GetGraphClientAsync();
        var body = new Dictionary<string, object>
        {
            ["displayName"] = displayName,
            ["description"] = description ?? "",
            ["visibility"] = visibility
        };

        var success = await client.PatchAsync($"/groups/{Uri.EscapeDataString(groupId)}", body);
        if (!success)
            return new M365GroupResult { Success = false, Message = "Failed to update M365 group." };

        _logger.LogInformation("Updated M365 group '{Name}' (id={Id})", displayName, groupId);
        return new M365GroupResult { Success = true, Message = $"Updated '{displayName}'." };
    }

    public async Task<M365GroupResult> DeleteGroupAsync(string groupId, string displayName)
    {
        var client = await GetGraphClientAsync();
        var success = await client.DeleteAsync($"/groups/{Uri.EscapeDataString(groupId)}");
        if (!success)
            return new M365GroupResult { Success = false, Message = "Failed to delete M365 group." };

        _logger.LogInformation("Deleted M365 group '{Name}' (id={Id})", displayName, groupId);
        return new M365GroupResult { Success = true, Message = $"Deleted '{displayName}'." };
    }

    public virtual async Task<List<M365GroupMember>> GetMembersAsync(string groupId)
    {
        return await GetPagedMembersAsync($"/groups/{Uri.EscapeDataString(groupId)}/members?$select=id,displayName,mail,userPrincipalName&$top=999");
    }

    public virtual async Task<List<M365GroupMember>> GetOwnersAsync(string groupId)
    {
        return await GetPagedMembersAsync($"/groups/{Uri.EscapeDataString(groupId)}/owners?$select=id,displayName,mail,userPrincipalName&$top=999");
    }

    /// <param name="actingUser">
    /// REQUIRED and not defaulted: the servicer decision needs a real principal, and a default
    /// would silently make every caller that forgot it unable to service - or invite an ambient
    /// lookup, which attributes a bypass to whoever is on the thread. Null refuses.
    /// </param>
    public Task<M365GroupResult> AddMemberAsync(string groupId, string memberUpnOrId, ClaimsPrincipal? actingUser) =>
        AddDirectoryObjectAsync(groupId, "members", memberUpnOrId, actingUser);

    /// <param name="actingUser">See <see cref="AddMemberAsync"/>: required, null refuses.</param>
    public Task<M365GroupResult> AddOwnerAsync(string groupId, string ownerUpnOrId, ClaimsPrincipal? actingUser) =>
        AddDirectoryObjectAsync(groupId, "owners", ownerUpnOrId, actingUser);

    /// <param name="actingUser">See <see cref="AddMemberAsync"/>: required, null refuses.</param>
    public Task<M365GroupResult> RemoveMemberAsync(string groupId, string memberObjectId, string memberIdentityForCheck, ClaimsPrincipal? actingUser) =>
        RemoveDirectoryObjectAsync(groupId, "members", memberObjectId, memberIdentityForCheck, actingUser);

    /// <param name="actingUser">See <see cref="AddMemberAsync"/>: required, null refuses.</param>
    public Task<M365GroupResult> RemoveOwnerAsync(string groupId, string ownerObjectId, string ownerIdentityForCheck, ClaimsPrincipal? actingUser) =>
        RemoveDirectoryObjectAsync(groupId, "owners", ownerObjectId, ownerIdentityForCheck, actingUser);

    /// <summary>
    /// Outcome of the gate: a <paramref name="Denial"/> to return to the caller, or null to
    /// proceed. <paramref name="ServicedNote"/> is set only when the identity was protected and an
    /// authorised servicer overrode the refusal; it must reach the page's audit call.
    /// </summary>
    private readonly record struct ProtectionGate(M365GroupResult? Denial, string? ServicedNote);

    private const string ProtectionUnavailable = "Protection check unavailable. Cannot verify if this member is protected.";

    /// <summary>
    /// The protected-principal gate, in the service immediately before the Graph write whoever the caller is ("UI hiding is
    /// not security", Constitution). It checks the directory object the write names, <paramref name="objectId"/>:
    /// a user through the shared cloud-user gate, by its object id; an object Graph says is no user - a device, a service
    /// principal, a group - has no user to protect and proceeds. <paramref name="identity"/> names the member in messages.
    /// Fail closed: an object Graph could not classify, an unavailable or ambiguous resolution, a failed or throwing check.
    /// </summary>
    /// <param name="mayBeAnotherKind">
    /// False when <paramref name="objectId"/> was resolved through Graph's user lookup, so it is a user; true when it came
    /// from the caller, and Graph is asked whether a user holds it.
    /// </param>
    private async Task<ProtectionGate> CheckProtectedAsync(
        GraphTokenClient client, string objectId, string identity, bool mayBeAnotherKind, ClaimsPrincipal? actingUser)
    {
        try
        {
            if (mayBeAnotherKind)
            {
                var (user, status) = await client.GetWithStatusAsync($"/users/{Uri.EscapeDataString(objectId)}?$select=id");
                user?.Dispose();
                if (status == System.Net.HttpStatusCode.NotFound)
                {
                    _logger.LogInformation(
                        "{Identity} ({ObjectId}) is not a user in the directory - no user to protect; proceeding", identity, objectId);
                    return new(null, null);
                }
                if (status != System.Net.HttpStatusCode.OK)
                {
                    _logger.LogWarning("Could not read whether {ObjectId} is a user ({Status}) - blocking", objectId, (int)status);
                    return new(new M365GroupResult { Success = false, Message = ProtectionUnavailable }, null);
                }
            }

            var decision = await ProtectedPrincipalServicing.ForCloudUserAsync(
                _protectedPrincipals, _servicers, objectId, identity, actingUser, ServicerModuleId);
            var message = decision.Refusal switch
            {
                ProtectedPrincipalServicing.CloudUserRefusal.None => null,
                ProtectedPrincipalServicing.CloudUserRefusal.Ambiguous => "Identity is ambiguous - matches multiple AD users.",
                ProtectedPrincipalServicing.CloudUserRefusal.CheckFailed => $"Protection check failed: {decision.FailReason}",
                ProtectedPrincipalServicing.CloudUserRefusal.Protected => "This member is a protected principal. Operation not permitted.",
                _ => ProtectionUnavailable,
            };
            return message is null
                ? new(null, decision.ServicedNote)
                : new(new M365GroupResult { Success = false, Message = message }, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Protected principal check failed for {Identity} - blocking as precaution", identity);
            return new(new M365GroupResult { Success = false, Message = $"Protection check error: {ex.Message}" }, null);
        }
    }

    private async Task<M365GroupResult> AddDirectoryObjectAsync(string groupId, string relationship, string identity, ClaimsPrincipal? actingUser)
    {
        if (string.IsNullOrWhiteSpace(identity))
            return new M365GroupResult { Success = false, Message = ProtectionUnavailable };

        var client = await GetGraphClientAsync();

        // `directoryObjects/{id}` wants an object id, and the identity that reaches here is whatever the operator typed - a
        // UPN or an email - so it is resolved to one first.
        var (objectId, resolveError) = await ResolveDirectoryObjectIdAsync(client, identity);
        if (objectId is null)
            return new M365GroupResult { Success = false, Message = resolveError };

        // The object checked is the object written: resolved first, then checked, then added.
        var gate = await CheckProtectedAsync(client, objectId, identity, mayBeAnotherKind: Guid.TryParse(identity.Trim(), out _), actingUser);
        if (gate.Denial is not null)
            return gate.Denial;

        var body = new Dictionary<string, object>
        {
            ["@odata.id"] = $"https://graph.microsoft.com/v1.0/directoryObjects/{Uri.EscapeDataString(objectId)}"
        };

        var ok = await client.PostNoContentAsync($"/groups/{Uri.EscapeDataString(groupId)}/{relationship}/$ref", body);
        if (!ok)
            return new M365GroupResult { Success = false, Message = $"Failed to add {identity}. Verify the Graph app has Group.ReadWrite.All." };

        var noun = relationship == "owners" ? "owner" : "member";
        _logger.LogInformation("Added {Noun} {Identity} to group {GroupId}", noun, identity, groupId);
        return new M365GroupResult { Success = true, Message = $"Added {noun} {identity}.", ServicedNote = gate.ServicedNote };
    }

    /// <summary>
    /// Turns whatever the operator typed into the directory object id Graph needs, or returns
    /// the reason it could not. Audit finding A10.
    /// </summary>
    /// <remarks>
    /// An id that is already an id is passed straight through, so a caller that has one does
    /// not pay for a lookup. Anything else is resolved through <c>/users/{identity}</c>, which
    /// accepts a UPN - that is the one Graph route that takes the shape a person types.
    ///
    /// sweep-d item 30: a guest's address, or a member's alias, is not a UPN, so that route says
    /// 404 for a person who exists. On a 404 for an address, the user is looked up by
    /// <c>mail</c> or by <c>proxyAddresses</c> (an advanced query: ConsistencyLevel eventual and
    /// $count), and exactly one match is required - none is "not found" naming the address, more
    /// than one refuses naming the count, because picking one would add a person the operator did
    /// not name. A 403 from either lookup keeps its "Not permitted to look up" message.
    ///
    /// Refuses rather than guessing. An identity that does not resolve is the operator's to
    /// correct, and the message says which part failed instead of blaming app permissions the
    /// way the old failure did.
    /// </remarks>
    internal static async Task<(string? objectId, string error)> ResolveDirectoryObjectIdAsync(
        GraphTokenClient client, string identity)
    {
        var trimmed = identity.Trim();
        if (Guid.TryParse(trimmed, out _))
            return (trimmed, "");

        var (doc, status) = await client.GetWithStatusAsync($"/users/{Uri.EscapeDataString(trimmed)}?$select=id");
        if (doc == null)
        {
            // Review of aud-a10: this lookup needs User.Read.All, which the module's own
            // config description did not previously mention - so an app registration
            // consented exactly as documented fails here with a 403 and no clue why. The
            // message names the permission rather than leaving an admin to guess.
            if (status == System.Net.HttpStatusCode.Forbidden)
                return (null, NotPermittedToLookUp(identity));

            if (status == System.Net.HttpStatusCode.NotFound && trimmed.Contains('@'))
                return await ResolveByAddressAsync(client, identity, trimmed);

            return (null, status == System.Net.HttpStatusCode.NotFound
                ? $"'{identity}' was not found in the directory."
                : $"Could not look up '{identity}': {(int)status} {status}.");
        }

        using var found = doc;
        var id = found.RootElement.TryGetProperty("id", out var prop) ? prop.GetString() : null;

        return string.IsNullOrWhiteSpace(id)
            ? (null, $"The directory returned no object id for '{identity}'.")
            : (id, "");
    }

    private static string NotPermittedToLookUp(string identity) =>
        $"Not permitted to look up '{identity}'. The Graph app registration "
        + "needs User.Read.All to resolve a name to a directory object.";

    /// <summary>
    /// sweep-d item 30. The user whose <c>mail</c> or one of whose <c>proxyAddresses</c> is
    /// <paramref name="address"/>, or the reason there is not exactly one.
    /// </summary>
    internal static async Task<(string? objectId, string error)> ResolveByAddressAsync(
        GraphTokenClient client, string identity, string address)
    {
        // OData string literal: a single quote is doubled. proxyAddresses compare case-insensitively,
        // so 'smtp:' matches the primary SMTP: entry as well as an alias.
        var literal = address.Replace("'", "''", StringComparison.Ordinal);
        var filter = $"mail eq '{literal}' or proxyAddresses/any(p:p eq 'smtp:{literal}')";
        var (doc, status) = await client.GetWithStatusAsync(
            $"/users?$filter={Uri.EscapeDataString(filter)}&$select=id&$count=true&$top=10",
            new Dictionary<string, string> { ["ConsistencyLevel"] = "eventual" });

        if (doc == null)
        {
            if (status == System.Net.HttpStatusCode.Forbidden)
                return (null, NotPermittedToLookUp(identity));

            return (null, status == System.Net.HttpStatusCode.NotFound
                ? NotFoundByAnyAddress(identity)
                : $"Could not look up '{identity}' by mail or proxy address: {(int)status} {status}.");
        }

        using var found = doc;
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (found.RootElement.TryGetProperty("value", out var value) && value.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var user in value.EnumerateArray())
            {
                var id = user.TryGetProperty("id", out var prop) ? prop.GetString() : null;
                if (string.IsNullOrWhiteSpace(id))
                    return (null, $"The directory returned a match with no object id for '{identity}'.");
                ids.Add(id);
            }
        }
        else
        {
            return (null, $"Could not look up '{identity}' by mail or proxy address: the directory's answer could not be read.");
        }

        // The count is the directory's, when it gives one: $top caps the list, not the matches.
        var count = found.RootElement.TryGetProperty("@odata.count", out var counted) && counted.TryGetInt32(out var n)
            ? Math.Max(n, ids.Count)
            : ids.Count;

        if (count == 0)
            return (null, NotFoundByAnyAddress(identity));

        if (count > 1)
            return (null, $"'{identity}' matches {count} directory users by mail or proxy address, so it does not "
                        + "name one person. Enter the user principal name of the one you mean.");

        return (ids.Single(), "");
    }

    private static string NotFoundByAnyAddress(string identity) =>
        $"'{identity}' was not found in the directory as a user principal name, a mail address or a proxy address.";

    private async Task<M365GroupResult> RemoveDirectoryObjectAsync(string groupId, string relationship, string objectId, string identityForCheck, ClaimsPrincipal? actingUser)
    {
        if (string.IsNullOrWhiteSpace(objectId))
            return new M365GroupResult { Success = false, Message = ProtectionUnavailable };

        var client = await GetGraphClientAsync();
        var gate = await CheckProtectedAsync(client, objectId, identityForCheck, mayBeAnotherKind: true, actingUser);
        if (gate.Denial is not null)
            return gate.Denial;

        var ok = await client.DeleteAsync($"/groups/{Uri.EscapeDataString(groupId)}/{relationship}/{Uri.EscapeDataString(objectId)}/$ref");
        if (!ok)
            return new M365GroupResult { Success = false, Message = $"Failed to remove {identityForCheck}. Verify the Graph app has Group.ReadWrite.All." };

        var noun = relationship == "owners" ? "owner" : "member";
        _logger.LogInformation("Removed {Noun} {Identity} from group {GroupId}", noun, identityForCheck, groupId);
        return new M365GroupResult { Success = true, Message = $"Removed {noun} {identityForCheck}.", ServicedNote = gate.ServicedNote };
    }

    private async Task<List<M365GroupMember>> GetPagedMembersAsync(string initialUrl)
    {
        var client = await GetGraphClientAsync();
        var results = new List<M365GroupMember>();
        var url = initialUrl;

        var isFirstPage = true;

        while (!string.IsNullOrEmpty(url))
        {
            // Audit finding A17. This was `GetAsync` with `if (doc == null) break;`, so ANY
            // page failure - including the very first - returned whatever had accumulated and
            // the caller could not tell a short list from a complete one. A membership list
            // that is silently missing people is the worst shape this can take: an operator
            // concludes someone is not a member and acts on it.
            //
            // Refuse rather than guess, which is the standing rule for inaccurate data. The
            // status is included because "it failed" and "you are throttled" need different
            // responses from whoever reads it.
            var (doc, status) = await client.GetWithStatusAsync(url);
            if (doc == null)
            {
                throw new InvalidOperationException(isFirstPage
                    ? $"Graph request for group members failed: {(int)status} {status}."
                    : $"Graph request for group members failed partway through: {(int)status} {status}. "
                      + $"{results.Count} member(s) had been read; the list would have been incomplete.");
            }

            isFirstPage = false;
            using var page = doc;

            foreach (var item in doc.RootElement.GetProperty("value").EnumerateArray())
                results.Add(ParseMember(item));

            if (doc.RootElement.TryGetProperty("@odata.nextLink", out var next))
            {
                var nextUrl = next.GetString();
                if (nextUrl != null && nextUrl.StartsWith("https://graph.microsoft.com/v1.0", StringComparison.OrdinalIgnoreCase))
                    nextUrl = nextUrl["https://graph.microsoft.com/v1.0".Length..];
                url = nextUrl;
            }
            else
            {
                url = null;
            }
        }

        return results.OrderBy(m => m.DisplayName).ToList();
    }

    private static M365Group ParseGroup(JsonElement item)
    {
        return new M365Group
        {
            Id = item.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
            DisplayName = item.TryGetProperty("displayName", out var dn) ? dn.GetString() ?? "" : "",
            Mail = item.TryGetProperty("mail", out var mail) ? mail.GetString() ?? "" : "",
            Description = item.TryGetProperty("description", out var desc) ? desc.GetString() ?? "" : "",
            Visibility = item.TryGetProperty("visibility", out var vis) ? vis.GetString() ?? "" : "",
            CreatedDateTime = item.TryGetProperty("createdDateTime", out var cd) ? cd.GetString() ?? "" : "",
            MailEnabled = item.TryGetProperty("mailEnabled", out var me) && me.ValueKind == JsonValueKind.True,
            SecurityEnabled = item.TryGetProperty("securityEnabled", out var se) && se.ValueKind == JsonValueKind.True
        };
    }

    private static M365GroupMember ParseMember(JsonElement item)
    {
        return new M365GroupMember
        {
            Id = item.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
            DisplayName = item.TryGetProperty("displayName", out var dn) ? dn.GetString() ?? "" : "",
            Mail = item.TryGetProperty("mail", out var mail) ? mail.GetString() ?? "" : "",
            UserPrincipalName = item.TryGetProperty("userPrincipalName", out var upn) ? upn.GetString() ?? "" : ""
        };
    }
}

public class M365Group
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Mail { get; set; } = "";
    public string Description { get; set; } = "";
    public string Visibility { get; set; } = "";
    public string CreatedDateTime { get; set; } = "";
    public bool MailEnabled { get; set; }
    public bool SecurityEnabled { get; set; }
}

public class M365GroupMember
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Mail { get; set; } = "";
    public string UserPrincipalName { get; set; } = "";
}

public class M365GroupResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";

    /// <summary>
    /// Set when the target was a protected principal and an authorised servicer overrode the
    /// refusal. Names the authorising group and the rules overridden.
    /// </summary>
    /// <remarks>
    /// The service decides but the PAGE audits, so the note has to survive the trip back. It
    /// belongs in the audit event's <c>extra</c> - never <c>errorDetail</c>, which is written as
    /// null on a successful action and would discard the record of who permitted the override.
    /// </remarks>
    public string? ServicedNote { get; set; }
}
