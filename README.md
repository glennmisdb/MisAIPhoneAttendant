# MIS AI Phone Attendant

This ASP.NET Core pilot answers a dedicated Google Ads number, screens business-software inquiries, and offers qualified callers a discovery appointment while they are still on the line. Twilio Conversation Relay handles speech and OpenAI helps with the conversation and intake. The application owns appointment execution and sales follow-up.

## Setup

Run the application with the .NET 9 SDK or the included Dockerfile.

```bash
dotnet run --project src/MisAIPhoneAttendant
```

Use [.env.example](.env.example) as a reference for environment variables. Set credentials through the host's secret configuration. ASP.NET Core does not load the example file.

| Settings | Purpose |
| --- | --- |
| `PUBLIC_BASE_URL`, `RELAY_SHARED_SECRET` | Public HTTPS origin with WebSocket support and a random relay secret |
| `TWILIO_AUTH_TOKEN`, `TWILIO_ACCOUNT_SID`, `TWILIO_NUMBER` | Signed Twilio requests and sending SMS from the dedicated E.164 number |
| `OPENAI_API_KEY`, `OPENAI_MODEL` | Conversational intake model |
| `CALENDLY_TOKEN`, `CALENDLY_EVENT_TYPE` | Calendly API token and full API URI for the event type |
| `SALES_USER`, `SALES_PASSWORD` | Staff access to the lead and SMS inbox and browser simulator. Use a strong password and serve over HTTPS. |
| `SALES_EMAIL`, `SMTP_*` | Sales distribution address and SMTP relay settings. Use a relay that accepts the configured SMTP authentication method. |
| `TRANSFER_NUMBER` | E.164 number for callers asking for a person. Set this before testing transfers. |
| `SALES_DATA_PATH` | Writable path for lead, SMS and sales-alert data. Mount a persistent volume for Docker and use a single app instance. |

Create a 20-minute Calendly event type with availability reserved for discovery calls. Use an event type with only the standard name and email fields. For the pilot, the agent uses Eastern time when the caller's time zone is unknown.

Set the Twilio number's incoming voice webhook to `POST /twilio/incoming-call` and its incoming messaging webhook to `POST /twilio/sms`, both on the configured public HTTPS origin. Keep `/twilio/conversation` available for the WebSocket upgrade. Complete messaging registration for your number and enable Twilio opt-out handling before sending texts.

## Staff workflow

- Open `/sales` for the lead list and two-way SMS inbox.
- Sales gets a **Meeting booked** email when Calendly accepts an appointment. If a qualified caller asks for a meeting and the call ends without a confirmed booking, sales gets a **Meeting requested** email. If a booking response is uncertain, check Calendly before rebooking.
- After booking, the voice agent asks permission to text a confirmation. Staff can respond to texts in the inbox.
- Use `/test` for AI conversation testing, `/api/leads` for the authenticated lead feed, and `/health` for a liveness check.

## Before routing Google Ads traffic

On the dedicated test number, place a qualified call that books a meeting, a qualified call that asks for follow-up without booking, and a call that does not qualify. Confirm Calendly creates a single appointment and sends the invitation. Check both sales alerts, reply to the confirmation SMS, and send a reply from the staff inbox. Test a rejected calendar request and a human transfer.

The pilot uses a single-instance JSON file store and retains caller text in the protected lead API. Deploy with persistent storage and restricted access. Configure a retention policy for pilot data. For a multi-instance deployment, move the store and alert queue to a shared transactional database. SMTP retry can resend an alert if the application stops after the relay accepts it but before the send is recorded.
