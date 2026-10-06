# Which Calls the Bot Can Join

> [!TIP]  
> Also read the [Compliance Recording Policies explanation](./recording-bot-policy.md)

There are three ways to get the recording bot into a call. Which one works depends on the type of
call and on whether a compliance recording policy is in place.

| Call type                          | Compliance policy | Join URL (`POST /joinCall`) | Added as participant (Teams app) |
|------------------------------------|:-----------------:|:---------------------------:|:--------------------------------:|
| Scheduled / ad-hoc Teams meeting   | ✅                | ✅                          | ✅                               |
| Group call                         | ✅                | ⚠️ not reliable             | ✅                               |
| Peer-to-peer call (Teams ↔ Teams)  | ✅                | ❌                          | ✅                               |
| PSTN call (phone ↔ Teams user)     | ✅                | ❌                          | ⚠️ untested                      |

## Compliance recording policy (recommended)

This is the scenario the sample is built for. When a user with an assigned compliance recording
policy places or receives a call, the Microsoft Teams platform invites the bot automatically. This
covers meetings, group calls, peer-to-peer calls and PSTN calls alike. The bot does not have to
know anything about the call in advance.

See the [Policy Guide](../guides/policy.md) for the setup.

## Join URL

The bot exposes `POST /joinCall` with a body like:

```json
{
  "joinURL": "https://teams.microsoft.com/l/meetup-join/...",
  "displayName": null
}
```

The bot parses the chat and meeting information out of the URL and joins. This only works for
calls that **have** a join URL, i.e. meetings. Peer-to-peer, group and PSTN calls have no join URL.

> [!NOTE]  
> Turning a peer-to-peer call into a group call and building a join URL from its `chatInfo` is not
> reliable: Microsoft Graph may return `chatInfo` as `null` for such calls.

If `displayName` is set, the bot joins as an anonymous guest. It may then wait in the lobby and
does not receive unmixed audio.

## Added as a participant (Teams app with calling)

Without a policy, you can package the bot as a Teams app whose
[manifest](https://learn.microsoft.com/microsoftteams/platform/resources/schema/manifest-schema)
enables calling (`"supportsCalling": true` on the bot). Once the app is installed, users can add
the bot to any call or meeting from the Teams client like any other participant, and the platform
sends the incoming call to the bot.

Inviting the bot programmatically through the Graph
[`participant: invite`](https://learn.microsoft.com/graph/api/participant-invite) API is Microsoft
Graph behaviour outside this sample. If you get `7505 Request authorization tenant mismatch`,
check that the identity in the request is the bot's app (client) ID, not an application instance's
object ID, and that the inviting application's token is issued for the tenant of the call.

See also [#57](https://github.com/LM-Development/OmniBot/issues/57) for the original discussion.
