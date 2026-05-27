package com.microsoft.agenthostprotocol

import com.microsoft.agenthostprotocol.generated.ActionEnvelope
import com.microsoft.agenthostprotocol.generated.ActionOrigin
import com.microsoft.agenthostprotocol.generated.ActionType
import com.microsoft.agenthostprotocol.generated.AgentSelection
import com.microsoft.agenthostprotocol.generated.ChangesetStatus
import com.microsoft.agenthostprotocol.generated.PartialSessionSummary
import com.microsoft.agenthostprotocol.generated.RootAgentsChangedAction
import com.microsoft.agenthostprotocol.generated.SessionStatus
import com.microsoft.agenthostprotocol.generated.StateAction
import com.microsoft.agenthostprotocol.generated.StateActionChangesetStatusChanged
import com.microsoft.agenthostprotocol.generated.StateActionRootAgentsChanged
import com.microsoft.agenthostprotocol.generated.StateActionSessionAgentChanged
import com.microsoft.agenthostprotocol.generated.StateActionSessionTitleChanged
import com.microsoft.agenthostprotocol.generated.StateActionUnknown
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.jsonObject
import org.junit.jupiter.api.Test
import kotlin.test.assertEquals
import kotlin.test.assertIs
import kotlin.test.assertNull

/**
 * Tests for [StateAction] and the [ActionEnvelope] wrapper. Critical for
 * forward compatibility: clients running an older protocol version must
 * decode unknown action types as [StateActionUnknown] (not throw) so that
 * reducers can no-op them and still apply the rest of a replay batch.
 */
class StateActionTest {
    private val json = Ahp.json

    @Test
    fun `known action round-trips through StateAction sealed interface`() {
        val action: StateAction = StateActionRootAgentsChanged(
            RootAgentsChangedAction(
                type = ActionType.ROOT_AGENTS_CHANGED,
                agents = emptyList(),
            ),
        )
        val encoded = json.encodeToString(StateAction.serializer(), action)
        val obj = json.parseToJsonElement(encoded).jsonObject
        assertEquals(JsonPrimitive("root/agentsChanged"), obj["type"])

        val decoded = json.decodeFromString(StateAction.serializer(), encoded)
        assertIs<StateActionRootAgentsChanged>(decoded)
    }

    @Test
    fun `unknown action type decodes to StateActionUnknown without throwing`() {
        // A future server may emit an action whose `type` is unknown to
        // this client. The reducer must be able to no-op it; that requires
        // the deserializer to never throw on unknown types.
        val futureWire = """{"type":"session/futureUnknownAction","payload":{}}"""
        val decoded = json.decodeFromString(StateAction.serializer(), futureWire)
        val unknown = assertIs<StateActionUnknown>(decoded)
        assertEquals("session/futureUnknownAction", unknown.type)
    }

    @Test
    fun `ActionEnvelope wraps a StateAction with serverSeq`() {
        // Channel-scoped envelope: every server-pushed action now carries
        // the channel URI it belongs to. Per-session actions like
        // `session/titleChanged` no longer include the session URI in their
        // payload — the channel identifies it instead.
        val envelopeJson = """{
            "channel": "ahp-session:/abc",
            "action": {
                "type": "session/titleChanged",
                "title": "New title"
            },
            "serverSeq": 42,
            "origin": {
                "clientId": "client-1",
                "clientSeq": 7
            }
        }""".trimIndent()

        val envelope = json.decodeFromString(ActionEnvelope.serializer(), envelopeJson)
        assertEquals("ahp-session:/abc", envelope.channel)
        assertEquals(42L, envelope.serverSeq)
        assertEquals(ActionOrigin(clientId = "client-1", clientSeq = 7L), envelope.origin)
        val title = assertIs<StateActionSessionTitleChanged>(envelope.action)
        assertEquals("New title", title.value.title)
    }

    @Test
    fun `Partial summary supports all-null wire payloads`() {
        // Partial<SessionSummary> models a partial-update notification.
        // Every field must be nullable; an empty payload is the wire
        // representation of "no changes" (rare but legal).
        val empty = json.decodeFromString(PartialSessionSummary.serializer(), "{}")
        assertNull(empty.title)
        assertNull(empty.status)

        // A typical partial: only `title` and `status` change.
        val partialJson = """{"title": "Renamed", "status": 1}"""
        val partial = json.decodeFromString(PartialSessionSummary.serializer(), partialJson)
        assertEquals("Renamed", partial.title)
        assertEquals(SessionStatus.IDLE, partial.status)
    }

    @Test
    fun `session agentChanged action carries an AgentSelection`() {
        // Verifies the v0.2 channels reorg added the session/agentChanged
        // action and that AgentSelection round-trips through ActionEnvelope.
        val wire = """{
            "channel": "ahp-session:/abc",
            "action": {
                "type": "session/agentChanged",
                "agent": { "uri": "ahp-customization:/my-agent" }
            },
            "serverSeq": 1
        }""".trimIndent()
        val envelope = json.decodeFromString(ActionEnvelope.serializer(), wire)
        val change = assertIs<StateActionSessionAgentChanged>(envelope.action)
        assertEquals(AgentSelection(uri = "ahp-customization:/my-agent"), change.value.agent)
    }

    @Test
    fun `changeset statusChanged action decodes on a changeset channel`() {
        // Changeset actions live on `ahp-changeset:` channels rather than
        // session channels. Verifies the new changeset/* action family is
        // wired through StateAction.
        val wire = """{
            "channel": "ahp-changeset:/abc/uncommitted",
            "action": {
                "type": "changeset/statusChanged",
                "status": "ready"
            },
            "serverSeq": 5
        }""".trimIndent()
        val envelope = json.decodeFromString(ActionEnvelope.serializer(), wire)
        assertEquals("ahp-changeset:/abc/uncommitted", envelope.channel)
        val change = assertIs<StateActionChangesetStatusChanged>(envelope.action)
        assertEquals(ChangesetStatus.READY, change.value.status)
    }
}
