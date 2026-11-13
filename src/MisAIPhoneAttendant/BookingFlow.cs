using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

public static class BookingFlow
{
    private const string Callback = "I’ll ask our sales team to follow up with you to arrange a time.";

    public static string Context(CallSession session) => session.BookedTime is { } booked
        ? $"Already booked for {Describe(booked, session.Intake?.TimeZone)}."
        : string.Join(" ", session.Slots.Select((slot, index) =>
            $"Option {index + 1}: {Describe(slot, session.Intake?.TimeZone)}."));

    public static async Task<AgentAnswer> ApplyAsync(CallSession session, AgentAnswer answer,
        PilotIntegrations integrations, LeadStore store, CancellationToken cancellationToken)
    {
        var previous = session.Intake;
        session.Intake = answer with
        {
            Name = Keep(answer.Name, previous?.Name),
            Company = Keep(answer.Company, previous?.Company),
            Email = Keep(answer.Email, previous?.Email),
            Project = Keep(answer.Project, previous?.Project),
            Timing = Keep(answer.Timing, previous?.Timing),
            TimeZone = session.Slots.Length > 0 || session.BookedTime is not null
                ? previous?.TimeZone ?? "" : Keep(answer.TimeZone, previous?.TimeZone),
            MeetingRequested = answer.MeetingRequested || previous?.MeetingRequested == true
        };
        var lead = session.Intake;
        if (session.BookedTime is { } existing)
        {
            if (session.AskedForSmsConsent && answer.SmsConsent && !session.ConfirmationAttempted &&
                !store.IsOptedOut(session.CallerNumber))
            {
                session.ConfirmationAttempted = true;
                try
                {
                    var body = $"MIS discovery call confirmed for {Describe(existing, lead.TimeZone)}. Reply STOP to opt out.";
                    var id = await integrations.SendSmsAsync(session.CallerNumber, body, cancellationToken);
                    store.Save(new SmsMessage(id, session.CallerNumber, "Outbound", body, DateTimeOffset.UtcNow));
                    return answer with { Text = "I sent the appointment confirmation to this number." };
                }
                catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException) { }
                catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { }
                return answer with { Text = "Your meeting is booked. I couldn’t verify that the text was sent. Please check your email invitation." };
            }
            return answer;
        }

        if (!lead.MeetingRequested || !lead.ProjectFit || string.IsNullOrWhiteSpace(lead.Project) ||
            string.IsNullOrWhiteSpace(lead.Company))
            return answer;
        if (session.BookingNeedsReview) return answer with { Text = Callback };
        if (string.IsNullOrWhiteSpace(lead.Name)) return answer with { Text = "What name should I put on the invitation?" };
        if (!MailAddress.TryCreate(lead.Email, out var address) ||
            !string.Equals(address.Address, lead.Email, StringComparison.OrdinalIgnoreCase))
            return answer with { Text = "What email address should I send the invitation to?" };

        try
        {
            if (answer.SelectedSlot >= 1 && answer.SelectedSlot <= session.Slots.Length)
            {
                var selected = session.Slots[answer.SelectedSlot - 1];
                // A timeout can happen after the provider accepts the booking. Leave reconciliation to sales.
                session.BookingNeedsReview = true;
                session.BookingId = await integrations.BookAsync(lead, selected, cancellationToken);
                session.BookedTime = selected;
                session.AskedForSmsConsent = integrations.SmsEnabled && PilotIntegrations.IsPhone(session.CallerNumber) &&
                    !store.IsOptedOut(session.CallerNumber);
                return answer with
                {
                    Text = $"You’re booked for {Describe(selected, lead.TimeZone)}. " +
                        "Your invitation will arrive by email." +
                        (session.AskedForSmsConsent ? " May I also text a confirmation to this number?" : "")
                };
            }

            if (session.Slots.Length == 0 || session.Slots.Any(slot => slot <= DateTimeOffset.UtcNow))
                session.Slots = await integrations.AvailabilityAsync(cancellationToken);
            if (session.Slots.Length == 0)
            {
                session.BookingNeedsReview = true;
                return answer with { Text = Callback };
            }

            return answer with { Text = $"I can offer {string.Join(" or ", session.Slots.Select(slot => Describe(slot, lead.TimeZone)))}. Which works for you?" };
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException ||
                                  (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            session.BookingNeedsReview = true;
            return answer with { Text = Callback };
        }
    }

    private static string Keep(string current, string? previous) =>
        string.IsNullOrWhiteSpace(current) ? previous ?? "" : current.Trim();

    internal static string Zone(string? value) =>
        TimeZoneInfo.TryFindSystemTimeZoneById(value ?? "", out _) &&
        (value!.Contains('/') || value == "UTC") ? value : "America/New_York";

    internal static string Describe(DateTimeOffset slot, string? zone)
    {
        var id = Zone(zone);
        var time = TimeZoneInfo.ConvertTimeBySystemTimeZoneId(slot, id);
        var label = id == "America/New_York" ? "Eastern time" : id.Split('/')[^1].Replace('_', ' ') + " time";
        return $"{time.ToString("dddd, MMMM d 'at' h:mm tt", CultureInfo.InvariantCulture)} {label}";
    }
}

public sealed class PilotIntegrations(HttpClient client, IConfiguration config)
{
    private string EventType => Setting("CALENDLY_EVENT_TYPE");
    internal bool SmsEnabled => !string.IsNullOrWhiteSpace(config["TWILIO_NUMBER"]);
    internal static bool IsPhone(string number) => Regex.IsMatch(number, @"^\+[1-9][0-9]{7,14}$");

    public async Task<DateTimeOffset[]> AvailabilityAsync(CancellationToken token)
    {
        var now = DateTimeOffset.UtcNow.AddMinutes(15);
        for (var week = 0; week < 2; week++)
        {
            var start = now.AddDays(week * 7);
            var end = start.AddDays(7).AddSeconds(-1);
            var query = $"event_type={Uri.EscapeDataString(EventType)}&start_time={Uri.EscapeDataString(start.UtcDateTime.ToString("O"))}&end_time={Uri.EscapeDataString(end.UtcDateTime.ToString("O"))}";
            using var result = await CalendlyAsync(HttpMethod.Get, "/event_type_available_times?" + query, null, token);
            var slots = result.RootElement.GetProperty("collection").EnumerateArray()
                .Where(item => item.GetProperty("status").GetString() == "available")
                .Select(item => item.GetProperty("start_time").GetDateTimeOffset()).Order().Take(2).ToArray();
            if (slots.Length > 0) return slots;
        }
        return [];
    }

    public async Task<string> BookAsync(AgentAnswer lead, DateTimeOffset slot, CancellationToken token)
    {
        using var result = await CalendlyAsync(HttpMethod.Post, "/invitees", new
        {
            event_type = EventType,
            start_time = slot.UtcDateTime.ToString("O"),
            invitee = new { name = lead.Name, email = lead.Email, timezone = BookingFlow.Zone(lead.TimeZone) }
        }, token);
        return result.RootElement.GetProperty("resource").GetProperty("uri").GetString()
            ?? throw new InvalidOperationException("Calendly did not return the booking identifier.");
    }

    public async Task<string> SendSmsAsync(string number, string body, CancellationToken token)
    {
        var account = Setting("TWILIO_ACCOUNT_SID");
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"https://api.twilio.com/2010-04-01/Accounts/{Uri.EscapeDataString(account)}/Messages.json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{account}:{Setting("TWILIO_AUTH_TOKEN")}")));
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["To"] = number, ["From"] = Setting("TWILIO_NUMBER"), ["Body"] = body
        });
        using var result = await SendAsync(request, token);
        return result.RootElement.GetProperty("sid").GetString() ?? throw new InvalidOperationException("Missing message SID.");
    }

    private async Task<JsonDocument> CalendlyAsync(HttpMethod method, string path, object? body, CancellationToken token)
    {
        using var request = new HttpRequestMessage(method, "https://api.calendly.com" + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Setting("CALENDLY_TOKEN"));
        if (body is not null) request.Content = JsonContent.Create(body);
        return await SendAsync(request, token);
    }

    private async Task<JsonDocument> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        using var response = await client.SendAsync(request, token);
        response.EnsureSuccessStatusCode();
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(token), cancellationToken: token);
    }

    private string Setting(string key) => !string.IsNullOrWhiteSpace(config[key])
        ? config[key]! : throw new InvalidOperationException($"Missing {key}.");
}
