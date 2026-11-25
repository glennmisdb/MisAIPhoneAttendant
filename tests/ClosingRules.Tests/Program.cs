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
