using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DireControl.Data;
using DireControl.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DireControl.Api.Services;

/// <summary>
/// Performs on-demand callsign lookups against HamDB (no key required) with
/// an optional fallback to the QRZ.com XML service when credentials are configured.
/// Results are cached in <see cref="Station.QrzLookupData"/>.
/// </summary>
public sealed partial class CallsignLookupService(
    IHttpClientFactory httpClientFactory,
    IServiceScopeFactory scopeFactory,
    IOptions<QrzOptions> qrzOptions,
    ILogger<CallsignLookupService> logger)
{
    // Valid amateur radio callsigns: 1–3 prefix chars, district digit, 1–3 suffix letters,
    // optional SSID. Rejects objects, tactical calls, and paths containing slashes.
    [GeneratedRegex(@"^[A-Z0-9]{1,3}[0-9][A-Z]{1,3}(-[0-9]{1,2})?$", RegexOptions.IgnoreCase)]
    private static partial Regex CallsignRegex();

    private string? _qrzSessionKey;

    /// <summary>Returns true if <paramref name="callsign"/> looks like a real amateur callsign.</summary>
    public static bool IsValidCallsign(string callsign)
    {
        if (callsign.Contains('/') || callsign.Contains('\\'))
            return false;

        return CallsignRegex().IsMatch(callsign.Trim());
    }

    /// <summary>
    /// Returns cached data if already present on the station record; otherwise performs
    /// a live lookup, caches the result, and returns it. Returns null when no record is
    /// found or the callsign is invalid.
    /// </summary>
    public async Task<QrzLookupData?> LookupAsync(string callsign, CancellationToken ct = default)
    {
        callsign = callsign.Trim().ToUpperInvariant();

        if (!IsValidCallsign(callsign))
            return null;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DireControlContext>();

        var station = await db.Stations.FirstOrDefaultAsync(s => s.Callsign == callsign, ct);

        // Return cached data immediately
        if (station?.QrzLookupData is not null)
            return station.QrzLookupData;

        // Live lookup — HamDB first, QRZ fallback
        var result = await QueryHamDbAsync(callsign, ct);

        if (result is null && qrzOptions.Value.IsConfigured)
            result = await QueryQrzAsync(callsign, ct);

        // Persist the result even if station is not yet in our DB (station may arrive later)
        if (result is not null && station is not null)
        {
            station.QrzLookupData = result;
            await db.SaveChangesAsync(ct);
        }

        return result;
    }

    // -------------------------------------------------------------------------
    // HamDB
    // -------------------------------------------------------------------------

    private async Task<QrzLookupData?> QueryHamDbAsync(string callsign, CancellationToken ct)
    {
        try
        {
            var http = httpClientFactory.CreateClient("HamDB");
            // HamDB expects an app name as the third path segment
            var response = await http.GetAsync(
                $"https://api.hamdb.org/{Uri.EscapeDataString(callsign)}/json/DireControl", ct);

            if (!response.IsSuccessStatusCode)
                return null;

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var hamdb = doc.RootElement.GetProperty("hamdb");

            var status = hamdb.GetProperty("messages").GetProperty("status").GetString();
            if (status is not "OK")
                return null;

            var cs = hamdb.GetProperty("callsign");
            string? GetStr(string prop) =>
                cs.TryGetProperty(prop, out var el) && el.ValueKind is not JsonValueKind.Null
                    ? NullIfEmpty(el.GetString()?.Trim())
                    : null;

            var fname = GetStr("fname");
            var lname = GetStr("name");
            var fullName = string.Join(" ", new[] { fname, lname }.Where(s => s is not null));

            return new QrzLookupData
            {
                Name = NullIfEmpty(fullName),
                City = GetStr("addr2"),
                State = GetStr("state"),
                LicenseClass = GetStr("class"),
                GridSquare = GetStr("grid"),
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "HamDB lookup failed for {Callsign}.", callsign);
            return null;
        }
    }

    // -------------------------------------------------------------------------
    // QRZ XML
    // -------------------------------------------------------------------------

    private async Task<QrzLookupData?> QueryQrzAsync(string callsign, CancellationToken ct)
    {
        try
        {
            var opts = qrzOptions.Value;
            if (!opts.IsConfigured)
                return null;

            var http = httpClientFactory.CreateClient("QRZ");

            if (_qrzSessionKey is null)
            {
                var loginResp = await http.GetAsync(
                    $"https://xmldata.qrz.com/xml/current/?username={Uri.EscapeDataString(opts.Username!)}" +
                    $"&password={Uri.EscapeDataString(opts.Password!)}&agent=DireControl", ct);

                if (!loginResp.IsSuccessStatusCode)
                    return null;

                var loginXml = XElement.Parse(await loginResp.Content.ReadAsStringAsync(ct));
                var ns = loginXml.Name.Namespace;
                _qrzSessionKey = loginXml.Element(ns + "Session")?.Element(ns + "Key")?.Value;

                if (_qrzSessionKey is null)
                    return null;
            }

            var lookupResp = await http.GetAsync(
                $"https://xmldata.qrz.com/xml/current/?s={_qrzSessionKey}&callsign={Uri.EscapeDataString(callsign)}", ct);

            if (!lookupResp.IsSuccessStatusCode)
                return null;

            var xml = XElement.Parse(await lookupResp.Content.ReadAsStringAsync(ct));
            var xns = xml.Name.Namespace;
            var csEl = xml.Element(xns + "Callsign");
            if (csEl is null)
                return null;

            string? Get(string prop) => NullIfEmpty(csEl.Element(xns + prop)?.Value?.Trim());

            var fname = Get("fname");
            var lname = Get("name");
            var fullName = string.Join(" ", new[] { fname, lname }.Where(s => s is not null));

            return new QrzLookupData
            {
                Name = NullIfEmpty(fullName),
                City = Get("addr2"),
                State = Get("state"),
                LicenseClass = Get("class"),
                GridSquare = Get("grid"),
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "QRZ lookup failed for {Callsign}.", callsign);
            _qrzSessionKey = null; // Force re-login on next attempt
            return null;
        }
    }

    private static string? NullIfEmpty(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s;
}
