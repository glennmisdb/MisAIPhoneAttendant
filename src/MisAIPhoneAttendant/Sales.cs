using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

public sealed record SmsMessage(string Id, string Number, string Direction, string Body, DateTimeOffset Time);
public sealed record SalesAlert(string Id, QualifiedLead Lead);
public sealed record SalesData(Dictionary<string, QualifiedLead> Leads, Dictionary<string, SmsMessage> Messages,
    Dictionary<string, SalesAlert> Pending, HashSet<string> Sent);

public sealed class LeadStore
{
    private readonly object gate = new();
    private readonly string path;

    public LeadStore(IConfiguration config)
    {
        path = Path.GetFullPath(config["SALES_DATA_PATH"] ?? "data/sales.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    }

    public void Save(QualifiedLead lead, bool completed = false) => Change(data =>
    {
        data.Leads[lead.CallSid] = lead;
        if (lead.Outcome == "Meeting booked" || (completed && lead.Outcome == "Meeting requested"))
        {
            var id = lead.CallSid + ":" + lead.Outcome;
            if (!data.Sent.Contains(id))
                data.Pending[id] = new SalesAlert(id, lead);
        }
    });

    public void Save(SmsMessage message) => Change(data => data.Messages.TryAdd(message.Id, message));

    public IReadOnlyCollection<QualifiedLead> All() =>
        Read().Leads.Values.OrderByDescending(lead => lead.CompletedAtUtc).ToArray();

    public IReadOnlyCollection<SmsMessage> Messages() =>
        Read().Messages.Values.OrderByDescending(message => message.Time).ToArray();

    public bool IsOptedOut(string number)
    {
        var last = Messages().FirstOrDefault(message => message.Number == number && message.Direction == "Inbound" &&
            (IsStop(message.Body) || message.Body.Trim().Equals("START", StringComparison.OrdinalIgnoreCase) ||
             message.Body.Trim().Equals("UNSTOP", StringComparison.OrdinalIgnoreCase)));
        return last is not null && IsStop(last.Body);
    }

    private static bool IsStop(string body) => body.Trim().ToUpperInvariant() is
        "STOP" or "STOPALL" or "UNSUBSCRIBE" or "CANCEL" or "END" or "QUIT" or "REVOKE" or "OPTOUT";

    public IReadOnlyCollection<SalesAlert> Pending() => Read().Pending.Values.ToArray();

    public void MarkSent(string id) => Change(data =>
    {
        data.Pending.Remove(id);
        data.Sent.Add(id);
    });

    private SalesData Read()
    {
        lock (gate)
            return File.Exists(path)
                ? JsonSerializer.Deserialize<SalesData>(File.ReadAllText(path)) ?? throw new IOException("Invalid sales data.")
                : new SalesData([], [], [], []);
    }

    private void Change(Action<SalesData> update)
    {
        lock (gate)
        {
            var data = Read();
            update(data);
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(data));
            File.Move(path + ".tmp", path, overwrite: true);
        }
    }
}

public sealed class SalesAlerts(LeadStore store, IConfiguration config, ILogger<SalesAlerts> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(config["SMTP_HOST"]) ||
            string.IsNullOrWhiteSpace(config["SALES_EMAIL"]) ||
            string.IsNullOrWhiteSpace(config["SMTP_FROM"]))
        {
            logger.LogWarning("Sales alerts will queue until SMTP_HOST, SALES_EMAIL and SMTP_FROM are configured.");
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        do
        {
            foreach (var alert in store.Pending())
            {
                try
                {
                    await SendAsync(alert, stoppingToken);
                    store.MarkSent(alert.Id);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning("Sales alert {AlertId} will retry ({ErrorType}).", alert.Id, ex.GetType().Name);
                }
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task SendAsync(SalesAlert alert, CancellationToken token)
    {
        var lead = alert.Lead;
        using var mail = new MailMessage(config["SMTP_FROM"]!, config["SALES_EMAIL"]!)
        {
            Subject = $"{lead.Outcome} | MIS advertising lead",
            Body = $"Name: {lead.Name}\nCompany: {lead.Company}\nPhone: {lead.CallerNumber}\nEmail: {lead.Email}\n" +
                   $"Project: {lead.Project}\nTiming: {lead.Timing}\nCall ID: {lead.CallSid}\n" +
                   (lead.BookedTime is { } time
                       ? $"Appointment: {BookingFlow.Describe(time, "America/New_York")}\nBooking: {lead.BookingId}"
                       : "Next step: check the calendar for an existing booking and follow up with the caller.")
        };
        using var smtp = new SmtpClient(config["SMTP_HOST"], config.GetValue("SMTP_PORT", 587))
        {
            EnableSsl = config.GetValue("SMTP_TLS", true)
        };
        if (!string.IsNullOrWhiteSpace(config["SMTP_USER"]))
            smtp.Credentials = new NetworkCredential(config["SMTP_USER"], config["SMTP_PASSWORD"]);
        await smtp.SendMailAsync(mail, token);
    }
}

public static class PilotSecurity
{
    public static async Task<bool> ValidTwilioRequestAsync(HttpContext context)
    {
        var config = context.RequestServices.GetRequiredService<IConfiguration>();
        var token = config["TWILIO_AUTH_TOKEN"];
        var publicUrl = config["PUBLIC_BASE_URL"]?.TrimEnd('/');
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(publicUrl)) return false;
        var request = context.Request;
        var payload = publicUrl + request.Path + request.QueryString;
        if (request.HasFormContentType)
        {
            foreach (var (key, values) in (await request.ReadFormAsync()).OrderBy(pair => pair.Key, StringComparer.Ordinal))
                foreach (var value in values.Distinct().Order(StringComparer.Ordinal))
                    payload += key + value;
        }
        var expected = Sign(payload, token);
        var actual = request.Headers["X-Twilio-Signature"].ToString();
        if (Equal(expected, actual)) return true;
        // The ConversationRelay upgrade may be signed using the WebSocket scheme.
        return context.WebSockets.IsWebSocketRequest && payload.StartsWith("https://", StringComparison.Ordinal) &&
            Equal(Sign("wss://" + payload[8..], token), actual);
    }

    public static bool ValidStaffRequest(HttpContext context)
    {
        var config = context.RequestServices.GetRequiredService<IConfiguration>();
        var password = config["SALES_PASSWORD"];
        if (string.IsNullOrEmpty(password)) return false;
        var user = config["SALES_USER"] ?? "sales";
        var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"));
        return Equal(expected, context.Request.Headers.Authorization.ToString());
    }

    private static string Sign(string value, string token) =>
        Convert.ToBase64String(HMACSHA1.HashData(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(value)));

    private static bool Equal(string left, string right) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right));
}

public static class SalesEndpoints
{
    public static void MapSalesEndpoints(this WebApplication app)
    {
        app.MapPost("/twilio/sms", async (HttpRequest request, LeadStore store) =>
        {
            var form = await request.ReadFormAsync();
            var id = form["MessageSid"].ToString();
            var number = form["From"].ToString();
            if (id.Length == 0 || !PilotIntegrations.IsPhone(number)) return Results.BadRequest();
            store.Save(new SmsMessage(id, number, "Inbound", form["Body"].ToString(), DateTimeOffset.UtcNow));
            return Results.Text("<Response/>", "application/xml");
        });

        app.MapGet("/sales", (HttpContext context, LeadStore store, PilotIntegrations integrations) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            static string H(string? value) => HtmlEncoder.Default.Encode(value ?? "");
            var leads = string.Join("", store.All().Select(lead => $"""
                <article><strong>{H(lead.Outcome)}</strong><h3>{H(lead.Company)} {H(lead.Name)}</h3>
                <p>{H(lead.Project)}</p><p>{H(lead.CallerNumber)} · {H(lead.Email)} · {H(lead.Timing)}</p>
                <p>{H(lead.BookedTime is { } time ? BookingFlow.Describe(time, "America/New_York") : "")}</p></article>
                """));
            var messages = string.Join("", store.Messages().Select(message => $"""
                <article><strong>{H(message.Direction)} · {H(message.Number)}</strong>
                <p>{H(message.Body)}</p><small>{H(message.Time.ToString("u"))}</small></article>
                """));
            var form = integrations.SmsEnabled ? """
                <form id="reply"><label>Phone number<input name="number" required placeholder="+16095550100"></label>
                <label>Message<textarea name="body" required maxlength="1500"></textarea></label>
                <button>Send SMS</button><output id="result" aria-live="polite"></output></form>
                <script>
                const form = document.querySelector('#reply');
                form.addEventListener('submit', async event => {
                    event.preventDefault();
                    const button = form.querySelector('button');
                    const result = document.querySelector('#result');
                    button.disabled = true;
                    result.textContent = 'Sending…';
                    try {
                        const response = await fetch('/sales/sms', {
                            method: 'POST', headers: {'Content-Type': 'application/json'},
                            body: JSON.stringify(Object.fromEntries(new FormData(form)))
                        });
                        if (response.ok) location.reload();
                        else result.textContent = await response.text();
                    } catch {
                        result.textContent = 'Delivery is uncertain. Check Twilio before retrying.';
                    } finally { button.disabled = false; }
                });
                </script>
                """ : "<p>SMS sending is unavailable until a Twilio number is configured.</p>";
            return Results.Content($$$"""
                <!doctype html><html lang="en"><head><meta charset="utf-8">
                <meta name="viewport" content="width=device-width,initial-scale=1"><title>MIS Sales Inbox</title>
                <style>
                *{box-sizing:border-box}body{font:16px system-ui;color:#152636;background:#f1f5f8;margin:0}
                main{max-width:1060px;margin:auto;padding:32px 24px}h1{margin:0 0 24px}
                .grid{display:grid;grid-template-columns:1fr 1fr;gap:28px}
                article,form{background:#fff;border:1px solid #d7e2eb;border-radius:10px;margin:0 0 12px;padding:16px;overflow-wrap:anywhere}
                h3,p{margin:8px 0}label{display:block;margin:0 0 12px}
                input,textarea{display:block;width:100%;padding:9px;margin-top:4px;font:inherit}
                button{background:#123e62;color:white;padding:10px 15px;border:0;border-radius:6px;font:inherit}
                output{display:block;margin-top:8px}@media(max-width:700px){.grid{grid-template-columns:1fr}}
                </style></head><body><main><h1>MIS Sales Inbox</h1><div class="grid">
                <section><h2>Call leads</h2>{{{(leads.Length == 0 ? "<p>No calls yet.</p>" : leads)}}}</section>
                <section><h2>Text messages</h2>{{{form}}}{{{(messages.Length == 0 ? "<p>No texts yet.</p>" : messages)}}}</section>
                </div></main></body></html>
                """, "text/html; charset=utf-8");
        });

        app.MapPost("/sales/sms", async (ReplyRequest reply, LeadStore store, PilotIntegrations integrations,
            CancellationToken token) =>
        {
            if (!integrations.SmsEnabled) return Results.Text("SMS is not configured.", statusCode: 503);
            if (reply.Number is null || !PilotIntegrations.IsPhone(reply.Number) ||
                string.IsNullOrWhiteSpace(reply.Body) || reply.Body.Length > 1500)
                return Results.Text("Enter a valid international phone number and a message up to 1,500 characters.", statusCode: 400);

            if (store.IsOptedOut(reply.Number)) return Results.Text("This number opted out of SMS.", statusCode: 409);

            try
            {
                var id = await integrations.SendSmsAsync(reply.Number, reply.Body, token);
                store.Save(new SmsMessage(id, reply.Number, "Outbound", reply.Body, DateTimeOffset.UtcNow));
                return Results.Ok();
            }
            catch (HttpRequestException ex) when (ex.StatusCode is >= HttpStatusCode.BadRequest and < HttpStatusCode.InternalServerError)
            {
                return Results.Text("Twilio rejected the SMS. Check the number and messaging configuration.", statusCode: 502);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or JsonException)
            {
                return Results.Text("Delivery is uncertain. Check Twilio before retrying.", statusCode: 502);
            }
        });
    }

    public sealed record ReplyRequest(string? Number, string? Body);
}
