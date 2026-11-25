using Microsoft.Extensions.Configuration;

using var client = new HttpClient();
var service = new OpenAiConversationService(client, new ConfigurationBuilder().Build());
async Task Check(string caller, AgentAnswer expected, bool pending = false)
{
    List<ConversationMessage> history = pending
        ? [new("assistant", ClosingRules.OfferText), new("caller", caller)]
        : [new("caller", caller)];
    var actual = await service.RespondAsync(history, CancellationToken.None);
    if (actual != expected) throw new Exception($"Unexpected response for {caller}: {actual}");
}
await Check("Goodbye", ClosingRules.Offer);
await Check("That's all", ClosingRules.Offer);
await Check("Yes", ClosingRules.Transfer, true);
await Check("No, thank you", ClosingRules.Farewell, true);
await Check("Talk to someone", ClosingRules.Transfer);
await Check("I want a representative", ClosingRules.Transfer, true);
if (TransferRules.RequestsHuman("I don't want a representative")) throw new Exception("Negative request transferred");
Console.WriteLine("Closing flow tests passed.");

var ask = new ConversationMessage("assistant", "What is your callback number?");
var readback = await service.RespondAsync([ask, new("caller", "six oh nine five five five zero one two three")], CancellationToken.None);
if (!readback.Text.Contains("6, 0, 9, 5, 5, 5, 0, 1, 2, 3")) throw new Exception("Spoken number not read back");
var corrected = await service.RespondAsync([new("assistant", readback.Text), new("caller", "No, 609-555-0124")], CancellationToken.None);
if (!corrected.Text.Contains("6, 0, 9, 5, 5, 5, 0, 1, 2, 4")) throw new Exception("Correction not read back");
var confirmed = await service.RespondAsync([new("assistant", corrected.Text), new("caller", "Yes")], CancellationToken.None);
if (!confirmed.OfferClosing) throw new Exception("Confirmed number did not offer a transfer");
var transfer = await service.RespondAsync([new("assistant", confirmed.Text), new("caller", "Yes")], CancellationToken.None);
if (!transfer.TransferToHuman) throw new Exception("Transfer after number confirmation failed");
var declined = await service.RespondAsync([new("assistant", confirmed.Text), new("caller", "No thank you")], CancellationToken.None);
if (!declined.EndCall) throw new Exception("Farewell after number confirmation failed");
