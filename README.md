# MIS AI Phone Attendant

A .NET 9 pilot that connects a new Twilio test number to Twilio ConversationRelay. It uses OpenAI to answer basic questions about Micro Integration Services, qualify prospective leads, and transfer callers to a person.

The pilot transfer destination is `+16093674818`.

## Responsibilities

Twilio supplies the phone number, answers the PSTN call, transcribes caller speech, speaks the application's response, and performs the final phone transfer. This application owns MIS knowledge, conversation rules, lead qualification, AI requests, lead summaries, and the transfer decision.

## Features

- Natural inbound voice conversation through Twilio ConversationRelay
- Explicit disclosure that the attendant is automated AI
- Restricted MIS knowledge and safety instructions
- Immediate transfer when a caller requests a person or reports an emergency
- Transfer handoff to the configured telephone number
- In-memory lead summaries and scoring
- Browser conversation simulator at `/test`
- Health endpoint at `/health`
- No call recording
- Docker support and GitHub Actions build validation

## Prerequisites

- .NET 9 SDK
- An OpenAI API key
- A Twilio account with ConversationRelay access
- A new voice-capable Twilio test number
- A public HTTPS host that supports secure WebSockets

## Configuration

Set these environment variables. Never commit their actual values.

| Variable | Purpose |
| --- | --- |
| `OPENAI_API_KEY` | OpenAI API credential |
| `OPENAI_MODEL` | Model name; defaults to `gpt-5-mini` |
| `PUBLIC_BASE_URL` | Public HTTPS URL without a trailing slash |
| `RELAY_SHARED_SECRET` | Long random value protecting the WebSocket endpoint |
| `TRANSFER_NUMBER` | Human transfer destination; pilot value is `+16093674818` |
| `LEAD_NOTIFICATION_EMAIL` | Reserved for the email-notification phase |

Copy `.env.example` as a reference. ASP.NET Core reads operating-system environment variables automatically.

## Run locally

```bash
dotnet run --project src/MisAIPhoneAttendant/MisAIPhoneAttendant.csproj
```

For local telephone testing, expose the application through a secure tunnel that supports WebSockets and set `PUBLIC_BASE_URL` to that HTTPS address. For example:

```text
PUBLIC_BASE_URL=https://example-tunnel-host
```

Open `http://localhost:5000/test` or the URL printed by `dotnet run` to test the conversation without Twilio.

## Configure the Twilio test number

1. Purchase a new voice-capable Twilio number.
2. Open the number's voice configuration.
3. For **A call comes in**, select **Webhook**.
4. Set the webhook to:
   ```text
   https://YOUR-PUBLIC-HOST/twilio/incoming-call
   ```
5. Select HTTP `POST`.
6. Save the configuration.
7. Call the new number.

The incoming-call endpoint returns TwiML that connects Twilio to:

```text
wss://YOUR-PUBLIC-HOST/twilio/conversation
```

When a transfer is required, the application sends ConversationRelay an `end` message with handoff data. Twilio posts that data to `/twilio/handoff`, which responds with TwiML that dials `+16093674818`.

## Endpoints

| Method | Endpoint | Purpose |
| --- | --- | --- |
| `POST` | `/twilio/incoming-call` | Returns ConversationRelay TwiML |
| `GET` WebSocket | `/twilio/conversation` | Exchanges caller prompts and AI responses |
| `POST` | `/twilio/handoff` | Transfers or closes the call |
| `GET` | `/api/leads` | Displays pilot in-memory lead summaries |
| `GET` | `/test` | Browser conversation simulator |
| `POST` | `/test/conversation` | Simulator AI endpoint |
| `GET` | `/health` | Health check |

## Production hardening

This is a pilot, not a finished production receptionist. Before using the published MIS number:

- Replace in-memory lead storage with SQL Server.
- Authenticate and restrict the lead API.
- Add Twilio signature validation in addition to the relay shared secret.
- Add persistent suppression and retention rules.
- Implement email or CRM notifications.
- Rate-limit public endpoints.
- Add monitoring, structured audit events, and alerting.
- Run scripted call evaluations for incorrect answers and missed transfers.
- Review disclosure, transcription, and retention language with counsel.

## Cost boundary

Twilio ConversationRelay performs speech recognition and text-to-speech. OpenAI receives text, not the telephone audio, in this architecture. The application does not record calls.
