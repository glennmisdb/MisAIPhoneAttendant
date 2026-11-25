using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient<OpenAiConversationService>();
builder.Services.AddSingleton<LeadStore>();

var app = builder.Build();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });

app.MapGet("/", () => Results.Ok(new
{
    service = "MIS AI Phone Attendant",
    status = "running",
    test = "/test"
}));

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

app.MapPost("/twilio/incoming-call", (IConfiguration config) =>
{
    var publicBaseUrl = Required(config, "PUBLIC_BASE_URL").TrimEnd('/');
    var relaySecret = Required(config, "RELAY_SHARED_SECRET");
    var webSocketBase = publicBaseUrl
        .Replace("https://", "wss://", StringComparison.OrdinalIgnoreCase)
        .Replace("http://", "ws://", StringComparison.OrdinalIgnoreCase);

    var response = new XElement("Response",
        new XElement("Connect",
            new XAttribute("action", $"{publicBaseUrl}/twilio/handoff"),
            new XAttribute("method", "POST"),
            new XElement("ConversationRelay",
                new XAttribute("url", $"{webSocketBase}/twilio/conversation?key={Uri.EscapeDataString(relaySecret)}"),
                new XAttribute("welcomeGreeting",
                    "Thank you for calling Micro Integration Services. I am MIS's automated AI assistant. " +
                    "I can answer basic questions, learn about your software needs, or connect you with someone. How may I help you?"),
                new XAttribute("language", "en-US"),
                new XAttribute("interruptible", "speech"),
                new XAttribute("interruptSensitivity", "medium"))));

    return Results.Text(response.ToString(SaveOptions.DisableFormatting), "application/xml");
});

app.MapPost("/twilio/handoff", async (HttpRequest request, IConfiguration config) =>
{
    var form = await request.ReadFormAsync();
    var handoffData = form["HandoffData"].ToString();
    var transferNumber = config["TRANSFER_NUMBER"] ?? "+16093674818";
    var shouldTransfer = handoffData.Contains("live-agent-handoff", StringComparison.OrdinalIgnoreCase);

    var response = shouldTransfer
        ? new XElement("Response",
            new XElement("Say", "Please hold while I connect you."),
            new XElement("Dial",
                new XAttribute("answerOnBridge", "true"),
                transferNumber))
        : new XElement("Response",
            new XElement("Say", "Thank you for calling Micro Integration Services. We appreciate your interest and look forward to helping you. Have a wonderful day!"),
            new XElement("Hangup"));

    return Results.Text(response.ToString(SaveOptions.DisableFormatting), "application/xml");
});

app.Map("/twilio/conversation", async (
    HttpContext context,
    IConfiguration config,
    OpenAiConversationService ai,
    LeadStore leads,
    ILoggerFactory loggerFactory) =>
{
    var logger = loggerFactory.CreateLogger("ConversationRelay");

    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    var expectedSecret = Required(config, "RELAY_SHARED_SECRET");
    if (!ConstantTimeEquals(context.Request.Query["key"], expectedSecret))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    var session = new CallSession();
    logger.LogInformation("ConversationRelay connection opened");

    Task<string?>? pendingReceive = null;
    var silenceRetries = 0;
    while (socket.State == WebSocketState.Open)
    {
        pendingReceive ??= ReceiveTextAsync(socket, context.RequestAborted);
        string? message;
        try
        {
            // Keep the same outstanding receive; cancelling it could abort the WebSocket.
            message = await pendingReceive.WaitAsync(TimeSpan.FromSeconds(25), context.RequestAborted);
            pendingReceive = null;
        }
        catch (TimeoutException)
        {
            logger.LogWarning("No relay input for call {CallSid}; retry {Retry}", session.CallSid, silenceRetries);
            var previous = session.Messages.LastOrDefault(m => m.Role == "assistant")?.Text;
            if (silenceRetries++ == 0)
            {
                await SendTextAsync(socket, "Are you still there? " + (previous ?? "How may I help you?"), true, context.RequestAborted);
            }
            else if (previous?.EndsWith(ClosingRules.OfferText, StringComparison.Ordinal) != true)
            {
                session.Messages.Add(new ConversationMessage("assistant", ClosingRules.OfferText));
                await SendTextAsync(socket, ClosingRules.OfferText, true, context.RequestAborted);
            }
            else
            {
                await SendEndAsync(socket, false, context.RequestAborted);
                break;
            }
            continue;
        }
        if (message is null) break;

        using var document = JsonDocument.Parse(message);
        var root = document.RootElement;
        var type = GetString(root, "type");
        logger.LogDebug("Relay event {EventType} for call {CallSid}", type, session.CallSid);

        if (type == "setup")
        {
            session.CallSid = GetString(root, "callSid");
            session.CallerNumber = GetString(root, "from");
            continue;
        }

        if (type != "prompt" ||
            !root.TryGetProperty("last", out var last) ||
            !last.GetBoolean())
        {
            continue;
        }

        var callerText = GetString(root, "voicePrompt");
        if (string.IsNullOrWhiteSpace(callerText))
        {
            logger.LogWarning("Empty final speech prompt for call {CallSid}", session.CallSid);
            await SendTextAsync(socket, "I'm sorry, I didn't catch that. Please repeat your answer.", true, context.RequestAborted);
            continue;
        }
        silenceRetries = 0;
        var timer = System.Diagnostics.Stopwatch.StartNew();

        session.Messages.Add(new ConversationMessage("caller", callerText));

        try
        {
            var answer = await ai.RespondAsync(session.Messages, context.RequestAborted);
            logger.LogInformation("Response ready for call {CallSid} in {ElapsedMs} ms", session.CallSid, timer.ElapsedMilliseconds);
            session.Messages.Add(new ConversationMessage("assistant", answer.Text));

            if (answer.EndCall)
            {
                // The handoff webhook speaks the final farewell before hanging up.
                await SendEndAsync(socket, false, context.RequestAborted);
                break;
            }

            await SendTextAsync(socket, answer.Text, true, context.RequestAborted);

            if (answer.TransferToHuman)
            {
                await Task.Delay(1400, context.RequestAborted);
                await SendEndAsync(socket, true, context.RequestAborted);
                break;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unable to produce AI response for call {CallSid}", session.CallSid);
            session.Messages.Add(new ConversationMessage("assistant", ClosingRules.OfferText));
            await SendTextAsync(socket, ClosingRules.OfferText, true, context.RequestAborted);
        }
    }

    var lead = LeadSummary.FromSession(session);
    leads.Save(lead);
    logger.LogInformation("Call {CallSid} completed with lead score {Score}", lead.CallSid, lead.Score);
});

app.MapGet("/api/leads", (LeadStore leads) => Results.Ok(leads.All()));

app.MapGet("/test", () => Results.Content(TestPage.Html, "text/html"));

app.MapPost("/test/conversation", async (
    TestRequest request,
    OpenAiConversationService ai,
    CancellationToken cancellationToken) =>
{
    var messages = request.Messages
        .Where(m => !string.IsNullOrWhiteSpace(m.Text))
        .TakeLast(20)
        .ToList();

    try
    {
        return Results.Ok(await ai.RespondAsync(messages, cancellationToken));
    }
    catch (Exception) when (!cancellationToken.IsCancellationRequested)
    {
        return Results.Ok(ClosingRules.Offer);
    }
});

app.Run();

static string Required(IConfiguration config, string key) =>
    !string.IsNullOrWhiteSpace(config[key])
        ? config[key]!
        : throw new InvalidOperationException($"Required setting {key} is missing.");

static string GetString(JsonElement element, string propertyName) =>
    element.TryGetProperty(propertyName, out var value) ? value.GetString() ?? "" : "";

static bool ConstantTimeEquals(string supplied, string expected)
{
    var left = Encoding.UTF8.GetBytes(supplied);
    var right = Encoding.UTF8.GetBytes(expected);
    return left.Length == right.Length &&
           System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(left, right);
}

static async Task<string?> ReceiveTextAsync(WebSocket socket, CancellationToken cancellationToken)
{
    var buffer = new byte[8192];
    using var stream = new MemoryStream();

    while (true)
    {
        var result = await socket.ReceiveAsync(buffer, cancellationToken);
        if (result.MessageType == WebSocketMessageType.Close) return null;
        stream.Write(buffer, 0, result.Count);
        if (result.EndOfMessage) break;
    }

    return Encoding.UTF8.GetString(stream.ToArray());
}

static Task SendTextAsync(
    WebSocket socket,
    string text,
    bool last,
    CancellationToken cancellationToken)
{
    var json = JsonSerializer.Serialize(new
    {
        type = "text",
        token = text,
        last,
        interruptible = true,
        preemptible = true
    });
    return socket.SendAsync(
        Encoding.UTF8.GetBytes(json),
        WebSocketMessageType.Text,
        true,
        cancellationToken);
}

static Task SendEndAsync(WebSocket socket, bool transfer, CancellationToken cancellationToken)
{
    var handoff = JsonSerializer.Serialize(new
    {
        reasonCode = transfer ? "live-agent-handoff" : "caller-confirmed-goodbye",
        reason = transfer ? "Caller requested or requires a person" : "Caller declined further assistance"
    });
    var json = JsonSerializer.Serialize(new { type = "end", handoffData = handoff });
    return socket.SendAsync(
        Encoding.UTF8.GetBytes(json),
        WebSocketMessageType.Text,
        true,
        cancellationToken);
}

public sealed class OpenAiConversationService(HttpClient httpClient, IConfiguration config)
{
    private const string Instructions = """
You are the automated telephone assistant for Micro Integration Services, Inc. (MIS).
Always clearly behave as an AI assistant. Be warm, concise, and easy to understand over a telephone.

Approved facts:
- MIS has solved business software problems since 1985.
- MIS rescues, supports, and modernizes legacy custom applications.
- MIS builds custom business software, internal applications, reports, customer and vendor portals.
- MIS connects applications, databases, carrier APIs, and third-party services.
- MIS supports shipping and logistics workflows involving USPS, UPS, and DHL.
- MIS automates spreadsheet handoffs, repeated data entry, and disconnected processes.
- MIS can work with MSPs and IT providers when their clients need application help.
- Relevant technologies can include FoxPro, Visual Basic 6, Microsoft Access, .NET, and SQL Server.

Conversation rules:
- Never invent prices, project estimates, availability, client names, guarantees, or technical conclusions.
- Never ask for passwords, credentials, personal financial information, or confidential customer data.
- Ask one question at a time.
- For a prospective project, collect naturally when relevant: name, company, callback number, email,
  business problem, application purpose, technology if known, users affected, operational impact,
  current support situation, source-code availability, and desired timing.
- Do not interrogate the caller or repeat questions already answered.
- If the caller requests a human, reports an existing-client production emergency, becomes frustrated,
  or asks for something outside approved facts, set transferToHuman to true.
- Keep spoken answers normally below 70 words.
- When asking for a callback number, explicitly say "callback number" or "phone number".
- The application reads back recognized numbers for confirmation. After confirmation,
  acknowledge it, continue with any remaining essential information, or set offerClosing
  to true if collection is complete. Never respond with empty text.
- Do not ask for the callback number again if it has already been confirmed.
- When the caller indicates they are finished, or you would otherwise say goodbye, set
  offerClosing to true. Do not say goodbye or end the call yet.
- If the previous assistant message offered to connect the caller, interpret
  the next caller response in that context. If they accept a transfer, set transferToHuman
  to true. If they clearly decline further assistance, set endCall to true.
- If they ask another question, answer it and continue normally. If their intention is ambiguous,
  clarify whether they want to speak with a representative.
- Never transfer merely because the caller mentions a representative in a negative response.
- Set endCall only after the caller has had the closing offer and clearly declined it.
- Do not include a final farewell in normal spoken responses.

Return only JSON with this shape:
{"text":"spoken response","transferToHuman":false,"offerClosing":false,"endCall":false}
""";

    public async Task<AgentAnswer> RespondAsync(
        IReadOnlyList<ConversationMessage> history,
        CancellationToken cancellationToken)
    {
        var closingPending = ClosingRules.IsPending(history);
        var callerText = history.LastOrDefault()?.Text ?? "";
        if (!closingPending && TransferRules.RequestsHuman(callerText))
            return ClosingRules.Transfer;
        if (closingPending && ClosingRules.IsDecline(callerText))
            return ClosingRules.Farewell;
        if (closingPending && ClosingRules.IsAcceptance(callerText))
            return ClosingRules.Transfer;
        if (!closingPending && ClosingRules.IsGoodbye(callerText))
            return ClosingRules.Offer;

        var phoneAnswer = CallbackRules.Handle(history);
        if (phoneAnswer is not null) return phoneAnswer;

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        var apiKey = config["OPENAI_API_KEY"];
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("Required setting OPENAI_API_KEY is missing.");
        var model = config["OPENAI_MODEL"] ?? "gpt-5-mini";
        var transcript = string.Join("\n", history.TakeLast(20).Select(m => $"{m.Role}: {m.Text}"));

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses");
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = JsonContent.Create(new
        {
            model,
            instructions = Instructions,
            input = transcript,
            text = new
            {
                format = new
                {
                    type = "json_schema",
                    name = "phone_response",
                    strict = true,
                    schema = new
                    {
                        type = "object",
                        properties = new
                        {
                            text = new { type = "string" },
                            transferToHuman = new { type = "boolean" },
                            offerClosing = new { type = "boolean" },
                            endCall = new { type = "boolean" }
                        },
                        required = new[] { "text", "transferToHuman", "offerClosing", "endCall" },
                        additionalProperties = false
                    }
                }
            }
        });

        using var response = await httpClient.SendAsync(request, deadline.Token);
        var body = await response.Content.ReadAsStringAsync(deadline.Token);
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(body);
        string? outputText = null;
        foreach (var outputItem in document.RootElement.GetProperty("output").EnumerateArray())
        {
            if (!outputItem.TryGetProperty("content", out var contentItems)) continue;
            foreach (var contentItem in contentItems.EnumerateArray())
            {
                if (contentItem.TryGetProperty("type", out var type) &&
                    type.GetString() == "output_text")
                {
                    outputText = contentItem.GetProperty("text").GetString();
                    if (!string.IsNullOrWhiteSpace(outputText)) break;
                }
            }
            if (!string.IsNullOrWhiteSpace(outputText)) break;
        }

        if (string.IsNullOrWhiteSpace(outputText))
            throw new InvalidOperationException("OpenAI returned no output text.");

        var answer = JsonSerializer.Deserialize<AgentAnswer>(
                   outputText,
                   new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
               ?? throw new InvalidOperationException("OpenAI returned an invalid response.");
        if (answer.TransferToHuman) return ClosingRules.Transfer;
        if (answer.EndCall) return closingPending ? ClosingRules.Farewell : ClosingRules.Offer;
        if (answer.OfferClosing || ClosingRules.IsGoodbye(answer.Text)) return ClosingRules.Offer;
        if (string.IsNullOrWhiteSpace(answer.Text))
            throw new InvalidOperationException("OpenAI returned empty spoken text.");
        return answer;
    }
}

public static class CallbackRules
{
    private const string Prefix = "I heard your callback number as ";
    private const string Retry = "Please repeat your callback number, one digit at a time.";
    public static AgentAnswer? Handle(IReadOnlyList<ConversationMessage> history)
    {
        if (history.Count < 2 || history[^2].Role != "assistant") return null;
        var previous = history[^2].Text;
        var caller = history[^1].Text.Trim();
        var confirmation = previous.StartsWith(Prefix, StringComparison.Ordinal);
        var asking = previous.Contains("callback number", StringComparison.OrdinalIgnoreCase) ||
                     previous.Contains("phone number", StringComparison.OrdinalIgnoreCase) ||
                     previous.Contains("telephone number", StringComparison.OrdinalIgnoreCase);
        if (!asking) return null;
        if (confirmation && (ClosingRules.IsAcceptance(caller) ||
            caller.TrimEnd('.', '!').ToLowerInvariant() is "correct" or "that is correct" or "that's correct" or "yes that's right" or "yes, that's right"))
            return new AgentAnswer("Thank you for confirming your number. " + ClosingRules.OfferText, false, true);
        var digits = Parse(caller);
        if (digits is not null)
            return new AgentAnswer(Prefix + string.Join(", ", digits.Select(c => c.ToString())) + ". Is that correct?", false);
        if (confirmation && ClosingRules.IsDecline(caller))
            return new AgentAnswer(Retry, false);
        if (caller.Any(char.IsDigit) || caller.Split(' ').Any(w => Words.ContainsKey(w.ToLowerInvariant())))
            return new AgentAnswer(Retry, false);
        return null;
    }

    private static readonly Dictionary<string, string> Words = new(StringComparer.OrdinalIgnoreCase)
    {
        ["zero"]="0", ["oh"]="0", ["one"]="1", ["two"]="2", ["three"]="3", ["four"]="4",
        ["five"]="5", ["six"]="6", ["seven"]="7", ["eight"]="8", ["nine"]="9"
    };

    public static string? Parse(string text)
    {
        var digits = new StringBuilder();
        foreach (System.Text.RegularExpressions.Match match in
                 System.Text.RegularExpressions.Regex.Matches(text, @"[0-9]+|[a-zA-Z]+"))
        {
            if (Words.TryGetValue(match.Value, out var digit)) digits.Append(digit);
            else if (match.Value.All(char.IsDigit)) digits.Append(match.Value);
        }
        return digits.Length == 10 || (digits.Length == 11 && digits[0] == '1') ? digits.ToString() : null;
    }
}

public static class TransferRules
{
    private static readonly string[] HumanPhrases =
    [
        "speak to someone", "speak with someone", "talk to someone", "talk with someone",
        "real person", "human", "representative", "transfer me", "connect me",
        "production is down", "system is down", "emergency"
    ];

    public static bool RequestsHuman(string text) =>
        !ClosingRules.IsNegative(text) &&
        HumanPhrases.Any(p => text.Contains(p, StringComparison.OrdinalIgnoreCase));
}

public static class ClosingRules
{
    public const string OfferText =
        "Before we end our call, would you like me to connect you with an MIS representative?";
    public static readonly AgentAnswer Offer = new(OfferText, false, true);
    public static readonly AgentAnswer Transfer = new(
        "Certainly. I will connect you with someone at Micro Integration Services now.", true);
    public static readonly AgentAnswer Farewell = new(
        "Thank you for calling Micro Integration Services. We appreciate your interest and look forward to helping you. Have a wonderful day!", false, false, true);

    public static bool IsPending(IReadOnlyList<ConversationMessage> history) =>
        history.Count >= 2 && history[^2].Role == "assistant" && history[^2].Text.EndsWith(OfferText, StringComparison.Ordinal);

    private static string Normalize(string text) =>
        text.Trim().TrimEnd('.', '!', '?', ',').ToLowerInvariant().Replace("’", "'");

    public static bool IsDecline(string text) => Normalize(text) is
        "no" or "no thanks" or "no thank you" or "no, thanks" or "no, thank you" or
        "nothing else" or "that's all" or "goodbye" or "bye" or "i'm all set";

    public static bool IsNegative(string text) =>
        IsDecline(text) || Normalize(text).StartsWith("no,") ||
        Normalize(text).Contains("don't need") || Normalize(text).Contains("don't want") ||
        Normalize(text).Contains("do not want");

    public static bool IsAcceptance(string text) => Normalize(text) is
        "yes" or "yes please" or "yes, please" or "sure" or "connect me" or "transfer me" ||
        TransferRules.RequestsHuman(text);

    public static bool IsGoodbye(string text)
    {
        var normalized = Normalize(text);
        return normalized is "bye" or "goodbye" or "that's all" or "i'm all set" or
            "have a nice day" or "have a wonderful day" ||
            normalized.EndsWith(" goodbye") || normalized.EndsWith(" have a wonderful day") ||
            normalized.EndsWith(" have a nice day");
    }
}

public sealed class LeadStore
{
    private readonly ConcurrentDictionary<string, QualifiedLead> _leads = new();

    public void Save(QualifiedLead lead) => _leads[lead.CallSid] = lead;

    public IReadOnlyCollection<QualifiedLead> All() =>
        _leads.Values.OrderByDescending(l => l.CompletedAtUtc).ToArray();
}

public sealed class CallSession
{
    public string CallSid { get; set; } = Guid.NewGuid().ToString("N");
    public string CallerNumber { get; set; } = "";
    public List<ConversationMessage> Messages { get; } = [];
}

public sealed record ConversationMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("text")] string Text);

public sealed record AgentAnswer(
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("transferToHuman")] bool TransferToHuman,
    [property: JsonPropertyName("offerClosing")] bool OfferClosing = false,
    [property: JsonPropertyName("endCall")] bool EndCall = false);

public sealed record QualifiedLead(
    string CallSid,
    string CallerNumber,
    int Score,
    string Transcript,
    DateTimeOffset CompletedAtUtc);

public static class LeadSummary
{
    public static QualifiedLead FromSession(CallSession session)
    {
        var transcript = string.Join("\n", session.Messages.Select(m => $"{m.Role}: {m.Text}"));
        var score = 0;
        if (session.Messages.Count >= 4) score += 10;
        if (ContainsAny(transcript, "legacy", "FoxPro", "VB6", "Access", "old application")) score += 25;
        if (ContainsAny(transcript, "shipping", "warehouse", "UPS", "USPS", "DHL")) score += 20;
        if (ContainsAny(transcript, "developer left", "developer retired", "unsupported")) score += 20;
        if (ContainsAny(transcript, "urgent", "down", "stopped", "cannot work")) score += 15;
        if (transcript.Contains("@", StringComparison.Ordinal)) score += 10;

        return new QualifiedLead(
            session.CallSid,
            session.CallerNumber,
            Math.Min(score, 100),
            transcript,
            DateTimeOffset.UtcNow);
    }

    private static bool ContainsAny(string text, params string[] terms) =>
        terms.Any(t => text.Contains(t, StringComparison.OrdinalIgnoreCase));
}

public sealed record TestRequest(List<ConversationMessage> Messages);

public static class TestPage
{
    public const string Html = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>MIS AI Phone Attendant Test</title>
<style>
body{font:16px system-ui;max-width:760px;margin:40px auto;padding:0 20px;color:#172033}
#log{border:1px solid #ccd3df;border-radius:8px;min-height:260px;padding:16px;white-space:pre-wrap}
form{display:flex;gap:8px;margin-top:12px}input{flex:1;padding:12px}button{padding:12px 18px}
</style>
</head>
<body>
<h1>MIS AI Phone Attendant</h1>
<p>Test the conversation before connecting a Twilio number.</p>
<div id="log">Assistant: Thank you for calling Micro Integration Services. How may I help you?</div>
<form><input id="message" autocomplete="off" placeholder="Type what the caller would say"><button>Send</button></form>
<script>
const messages=[];const log=document.querySelector('#log');const input=document.querySelector('#message');
document.querySelector('form').onsubmit=async e=>{
 e.preventDefault();const text=input.value.trim();if(!text)return;input.value='';
 messages.push({role:'caller',text});log.textContent+='\n\nCaller: '+text;
 const r=await fetch('/test/conversation',{method:'POST',headers:{'content-type':'application/json'},body:JSON.stringify({messages})});
 const a=await r.json();messages.push({role:'assistant',text:a.text});
 log.textContent+='\n\nAssistant: '+a.text+(a.transferToHuman?'\n[TRANSFER TO HUMAN]':a.endCall?'\n[CALL ENDED]':'');if(a.transferToHuman||a.endCall){input.disabled=true;document.querySelector("button").disabled=true;}window.scrollTo(0,document.body.scrollHeight);
};
</script>
</body>
</html>
""";
}
