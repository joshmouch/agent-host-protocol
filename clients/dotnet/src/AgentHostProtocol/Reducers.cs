// Pure state reducers — a faithful port of the Go client's reducers.go,
// which in turn mirrors the canonical TypeScript reducers. Each reducer
// mutates the supplied state in place and reports whether it applied.
#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json;

namespace Microsoft.AgentHostProtocol;

/// <summary>What happened when a reducer was asked to apply an action.</summary>
public enum ReduceOutcome
{
    /// <summary>The action was applied and the state was mutated.</summary>
    Applied,

    /// <summary>The action was recognized but had no effect against this state.</summary>
    NoOp,

    /// <summary>The action targets a different scope (e.g. a session action passed to the root reducer).</summary>
    OutOfScope,
}

/// <summary>
/// Pure reducers for the Agent Host Protocol. <see cref="ApplyToRoot"/>,
/// <see cref="ApplyToSession"/>, <see cref="ApplyToTerminal"/>, and
/// <see cref="ApplyToChangeset"/> apply a <see cref="StateAction"/> to the
/// matching state tree in place.
/// </summary>
public static class Reducers
{
    // ─── Injectable timestamp ──────────────────────────────────────────────

    private static volatile Func<long> s_now = () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>
    /// Overrides the function reducers call to stamp <c>summary.modifiedAt</c>.
    /// Useful for tests that need deterministic output. Pass <see langword="null"/>
    /// to restore the default (current Unix time in milliseconds).
    /// </summary>
    public static void SetNowProvider(Func<long>? provider)
    {
        s_now = provider ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    private static long NowMs() => s_now();

    private static string NowIso() =>
        DateTimeOffset.FromUnixTimeMilliseconds(NowMs()).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture);

    // Mirrors Go's `append([]T(nil), src...)`: a null source yields a null
    // result (which serializes as absent / null and is stripped by the
    // conformance harness), a non-null source yields a shallow copy.
    private static List<T>? CopyList<T>(List<T>? src) => src is null ? null : new List<T>(src);

    // ─── Status helpers ────────────────────────────────────────────────────

    // Covers the mutually-exclusive activity bits (bits 0–4) of SessionStatus.
    private const SessionStatus StatusActivityMask = (SessionStatus)((1u << 5) - 1);

    private static SessionStatus WithStatusFlag(SessionStatus status, SessionStatus flag, bool set) =>
        set ? status | flag : status & ~flag;

    // ─── Tool-call helpers ─────────────────────────────────────────────────

    private readonly record struct ToolCallCommon(
        string Id,
        string Name,
        string DisplayName,
        ToolCallContributor? Contributor,
        Dictionary<string, JsonElement>? Meta);

    private static ToolCallCommon ToolCallMeta(ToolCallState tc) => tc.Value switch
    {
        ToolCallStreamingState v => new(v.ToolCallId, v.ToolName, v.DisplayName, v.Contributor, v.Meta),
        ToolCallPendingConfirmationState v => new(v.ToolCallId, v.ToolName, v.DisplayName, v.Contributor, v.Meta),
        ToolCallRunningState v => new(v.ToolCallId, v.ToolName, v.DisplayName, v.Contributor, v.Meta),
        ToolCallPendingResultConfirmationState v => new(v.ToolCallId, v.ToolName, v.DisplayName, v.Contributor, v.Meta),
        ToolCallCompletedState v => new(v.ToolCallId, v.ToolName, v.DisplayName, v.Contributor, v.Meta),
        ToolCallCancelledState v => new(v.ToolCallId, v.ToolName, v.DisplayName, v.Contributor, v.Meta),
        _ => default,
    };

    private static (StringOrMarkdown Invocation, string? ToolInput) ToolCallInvocationAndInput(ToolCallState tc) =>
        tc.Value switch
        {
            ToolCallStreamingState v => (v.InvocationMessage ?? new StringOrMarkdown(), null),
            ToolCallPendingConfirmationState v => (v.InvocationMessage, v.ToolInput),
            ToolCallRunningState v => (v.InvocationMessage, v.ToolInput),
            ToolCallPendingResultConfirmationState v => (v.InvocationMessage, v.ToolInput),
            _ => (new StringOrMarkdown(), null),
        };

    private static string ToolCallId(ToolCallState tc) => ToolCallMeta(tc).Id;

    private static bool HasPendingToolCallConfirmation(ChatState state)
    {
        if (state.ActiveTurn is null)
        {
            return false;
        }

        foreach (ResponsePart part in state.ActiveTurn.ResponseParts)
        {
            if (part.Value is not ToolCallResponsePart tc)
            {
                continue;
            }

            if (tc.ToolCall.Value is ToolCallPendingConfirmationState or ToolCallPendingResultConfirmationState)
            {
                return true;
            }
        }

        return false;
    }

    private static SessionStatus ChatSummaryStatus(ChatState state, SessionStatus? terminal)
    {
        SessionStatus activity;
        if (terminal is not null)
        {
            activity = terminal.Value;
        }
        else if ((state.InputRequests?.Count ?? 0) > 0 || HasPendingToolCallConfirmation(state))
        {
            activity = SessionStatus.InputNeeded;
        }
        else if (state.ActiveTurn is not null)
        {
            activity = SessionStatus.InProgress;
        }
        else
        {
            activity = SessionStatus.Idle;
        }

        return (state.Status & ~StatusActivityMask) | activity;
    }

    private static void RefreshChatStatus(ChatState state) =>
        state.Status = ChatSummaryStatus(state, null);

    private static void TouchModifiedChat(ChatState state) =>
        state.ModifiedAt = NowIso();

    // ─── Active-turn helpers ───────────────────────────────────────────────

    private static ReduceOutcome EndTurn(
        ChatState state,
        string turnId,
        TurnState turnState,
        SessionStatus? terminalStatus,
        ErrorInfo? errInfo)
    {
        if (state.ActiveTurn is null || state.ActiveTurn.Id != turnId)
        {
            return ReduceOutcome.NoOp;
        }

        ActiveTurn active = state.ActiveTurn;
        state.ActiveTurn = null;

        var parts = new List<ResponsePart>(active.ResponseParts.Count);
        foreach (ResponsePart part in active.ResponseParts)
        {
            if (part.Value is not ToolCallResponsePart tc)
            {
                parts.Add(part);
                continue;
            }

            if (tc.ToolCall.Value is ToolCallCompletedState or ToolCallCancelledState)
            {
                parts.Add(part);
                continue;
            }

            ToolCallCommon common = ToolCallMeta(tc.ToolCall);
            (StringOrMarkdown invocation, string? toolInput) = ToolCallInvocationAndInput(tc.ToolCall);
            var cancelled = new ToolCallCancelledState
            {
                Status = ToolCallStatus.Cancelled,
                ToolCallId = common.Id,
                ToolName = common.Name,
                DisplayName = common.DisplayName,
                Contributor = common.Contributor,
                Meta = common.Meta,
                InvocationMessage = invocation,
                ToolInput = toolInput,
                Reason = ToolCallCancellationReason.Skipped,
            };
            parts.Add(new ResponsePart(new ToolCallResponsePart
            {
                Kind = ResponsePartKind.ToolCall,
                ToolCall = new ToolCallState(cancelled),
            }));
        }

        var turn = new Turn
        {
            Id = active.Id,
            Message = active.Message,
            ResponseParts = parts,
            Usage = active.Usage,
            State = turnState,
            Error = errInfo,
        };

        state.Turns.Add(turn);
        state.InputRequests = null;
        TouchModifiedChat(state);
        state.Status = ChatSummaryStatus(state, terminalStatus);
        return ReduceOutcome.Applied;
    }

    private static void UpsertChatInputRequest(ChatState state, ChatInputRequest req)
    {
        List<ChatInputRequest> existing = state.InputRequests ?? new List<ChatInputRequest>();
        int found = existing.FindIndex(r => r.Id == req.Id);
        if (found >= 0)
        {
            req.Answers ??= existing[found].Answers;
            existing[found] = req;
        }
        else
        {
            existing.Add(req);
        }

        state.InputRequests = existing;
        state.Status = ChatSummaryStatus(state, null);
        TouchModifiedChat(state);
        state.Status = WithStatusFlag(state.Status, SessionStatus.IsRead, false);
    }

    // ─── Customization helpers ─────────────────────────────────────────────

    private static bool TryCustomizationId(Customization c, out string id)
    {
        switch (c.Value)
        {
            case PluginCustomization v:
                id = v.Id;
                return true;
            case DirectoryCustomization v:
                id = v.Id;
                return true;
            default:
                id = string.Empty;
                return false;
        }
    }

    private static bool TryChildCustomizationId(ChildCustomization c, out string id)
    {
        switch (c.Value)
        {
            case AgentCustomization v: id = v.Id; return true;
            case SkillCustomization v: id = v.Id; return true;
            case PromptCustomization v: id = v.Id; return true;
            case RuleCustomization v: id = v.Id; return true;
            case HookCustomization v: id = v.Id; return true;
            case McpServerCustomization v: id = v.Id; return true;
            default: id = string.Empty; return false;
        }
    }

    private static List<ChildCustomization>? ContainerChildren(Customization c) => c.Value switch
    {
        PluginCustomization v => v.Children,
        DirectoryCustomization v => v.Children,
        _ => null,
    };

    private static void SetContainerEnabled(Customization c, bool enabled)
    {
        switch (c.Value)
        {
            case PluginCustomization v: v.Enabled = enabled; break;
            case DirectoryCustomization v: v.Enabled = enabled; break;
        }
    }

    private static bool ApplyToggle(List<Customization> list, string id, bool enabled)
    {
        foreach (Customization c in list)
        {
            if (TryCustomizationId(c, out string got) && got == id)
            {
                SetContainerEnabled(c, enabled);
                return true;
            }
        }

        return false;
    }

    // ─── Active-turn mutation helpers ──────────────────────────────────────

    private static ReduceOutcome UpdateToolCall(
        ChatState state,
        string turnId,
        string targetToolCallId,
        Func<ToolCallState, ToolCallState> updater)
    {
        if (state.ActiveTurn is null || state.ActiveTurn.Id != turnId)
        {
            return ReduceOutcome.NoOp;
        }

        List<ResponsePart> parts = state.ActiveTurn.ResponseParts;
        for (int i = 0; i < parts.Count; i++)
        {
            if (parts[i].Value is not ToolCallResponsePart tc)
            {
                continue;
            }

            if (ToolCallId(tc.ToolCall) == targetToolCallId)
            {
                tc.ToolCall = updater(tc.ToolCall);
                return ReduceOutcome.Applied;
            }
        }

        return ReduceOutcome.NoOp;
    }

    private static ReduceOutcome UpdateResponsePart(
        ChatState state,
        string turnId,
        string partId,
        Action<ResponsePart> updater)
    {
        if (state.ActiveTurn is null || state.ActiveTurn.Id != turnId)
        {
            return ReduceOutcome.NoOp;
        }

        foreach (ResponsePart part in state.ActiveTurn.ResponseParts)
        {
            string id = part.Value switch
            {
                ToolCallResponsePart v => ToolCallId(v.ToolCall),
                MarkdownResponsePart v => v.Id,
                ReasoningResponsePart v => v.Id,
                _ => string.Empty,
            };

            if (id.Length > 0 && id == partId)
            {
                updater(part);
                return ReduceOutcome.Applied;
            }
        }

        return ReduceOutcome.NoOp;
    }

    // ─── Root Reducer ──────────────────────────────────────────────────────

    /// <summary>
    /// Applies <paramref name="action"/> to the <see cref="RootState"/> in place.
    /// Returns <see cref="ReduceOutcome.OutOfScope"/> for actions that target a
    /// different state tree.
    /// </summary>
    public static ReduceOutcome ApplyToRoot(RootState state, StateAction action)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(action);
        switch (action.Value)
        {
            case RootAgentsChangedAction a:
                state.Agents = CopyList(a.Agents)!;
                return ReduceOutcome.Applied;
            case RootActiveSessionsChangedAction a:
                state.ActiveSessions = a.ActiveSessions;
                return ReduceOutcome.Applied;
            case RootTerminalsChangedAction a:
                state.Terminals = CopyList(a.Terminals)!;
                return ReduceOutcome.Applied;
            case RootConfigChangedAction a:
                if (state.Config is null)
                {
                    return ReduceOutcome.NoOp;
                }

                state.Config.Values = MergeConfig(state.Config.Values, a.Config, a.Replace);
                return ReduceOutcome.Applied;
        }

        return ReduceOutcome.OutOfScope;
    }

    // Shared config merge for the root and session `configChanged` actions:
    // when `replace` is set (or no values exist yet) start fresh, otherwise
    // mutate the existing map in place; then overlay the incoming entries.
    private static Dictionary<string, JsonElement> MergeConfig(
        Dictionary<string, JsonElement>? current,
        Dictionary<string, JsonElement> incoming,
        bool? replace)
    {
        Dictionary<string, JsonElement> values = replace == true || current is null
            ? new Dictionary<string, JsonElement>(incoming.Count)
            : current;

        foreach (KeyValuePair<string, JsonElement> kv in incoming)
        {
            values[kv.Key] = kv.Value;
        }

        return values;
    }

    // ─── Session Reducer ───────────────────────────────────────────────────

    /// <summary>
    /// Applies <paramref name="action"/> to the <see cref="SessionState"/> in
    /// place. Returns <see cref="ReduceOutcome.OutOfScope"/> for actions that
    /// target a different state tree.
    /// </summary>
    public static ReduceOutcome ApplyToSession(SessionState state, StateAction action)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(action);
        switch (action.Value)
        {
            case SessionReadyAction:
                state.Lifecycle = SessionLifecycle.Ready;
                return ReduceOutcome.Applied;
            case SessionCreationFailedAction a:
                state.Lifecycle = SessionLifecycle.CreationFailed;
                state.CreationError = a.Error;
                return ReduceOutcome.Applied;
            case SessionTitleChangedAction a:
                state.Title = a.Title;
                return ReduceOutcome.Applied;
            case SessionIsReadChangedAction a:
                state.Status = WithStatusFlag(state.Status, SessionStatus.IsRead, a.IsRead);
                return ReduceOutcome.Applied;
            case SessionIsArchivedChangedAction a:
                state.Status = WithStatusFlag(state.Status, SessionStatus.IsArchived, a.IsArchived);
                return ReduceOutcome.Applied;
            case SessionActivityChangedAction a:
                state.Activity = a.Activity;
                return ReduceOutcome.Applied;
            case SessionChangesetsChangedAction a:
                state.Changesets = CopyList(a.Changesets);
                return ReduceOutcome.Applied;
            case SessionConfigChangedAction a:
                if (state.Config is null)
                {
                    return ReduceOutcome.NoOp;
                }

                state.Config.Values = MergeConfig(state.Config.Values, a.Config, a.Replace);
                return ReduceOutcome.Applied;
            case SessionMetaChangedAction a:
                state.Meta = a.Meta;
                return ReduceOutcome.Applied;
            case SessionServerToolsChangedAction a:
                state.ServerTools = CopyList(a.Tools)!;
                return ReduceOutcome.Applied;
            case SessionActiveClientSetAction a:
            {
                // Upsert keyed by clientId: replace the existing entry with the
                // same clientId, otherwise append. Mirrors the TS reducer.
                int idx = state.ActiveClients.FindIndex(c => c.ClientId == a.ActiveClient.ClientId);
                if (idx < 0)
                {
                    state.ActiveClients.Add(a.ActiveClient);
                }
                else
                {
                    state.ActiveClients[idx] = a.ActiveClient;
                }

                return ReduceOutcome.Applied;
            }
            case SessionActiveClientRemovedAction a:
            {
                // Remove the entry matching clientId; no-op when none matches.
                int idx = state.ActiveClients.FindIndex(c => c.ClientId == a.ClientId);
                if (idx < 0)
                {
                    return ReduceOutcome.NoOp;
                }

                state.ActiveClients.RemoveAt(idx);
                return ReduceOutcome.Applied;
            }
            case SessionCustomizationsChangedAction a:
                state.Customizations = CopyList(a.Customizations);
                return ReduceOutcome.Applied;
            case SessionCustomizationToggledAction a:
                if (state.Customizations is null)
                {
                    return ReduceOutcome.NoOp;
                }

                return ApplyToggle(state.Customizations, a.Id, a.Enabled)
                    ? ReduceOutcome.Applied
                    : ReduceOutcome.NoOp;
            case SessionCustomizationUpdatedAction a:
                return ApplyCustomizationUpdated(state, a);
            case SessionCustomizationRemovedAction a:
                return ApplyCustomizationRemoved(state, a);
            case SessionMcpServerStateChangedAction a:
                return ApplyMcpServerStateChanged(state, a);
            case SessionChatAddedAction a:
                return ApplySessionChatAdded(state, a);
            case SessionChatRemovedAction a:
                return ApplySessionChatRemoved(state, a);
            case SessionChatUpdatedAction a:
                return ApplySessionChatUpdated(state, a);
            case SessionDefaultChatChangedAction a:
                state.DefaultChat = a.DefaultChat;
                return ReduceOutcome.Applied;
        }

        return ReduceOutcome.OutOfScope;
    }

    // ─── Chat-channel reducer ──────────────────────────────────────────────

    /// <summary>
    /// Applies <paramref name="action"/> to the <see cref="ChatState"/> in
    /// place. Returns <see cref="ReduceOutcome.OutOfScope"/> for actions that
    /// target a different state tree.
    /// </summary>
    public static ReduceOutcome ApplyToChat(ChatState state, StateAction action)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(action);
        switch (action.Value)
        {
            case ChatTurnStartedAction a:
                return ApplyChatTurnStarted(state, a);
            case ChatDeltaAction a:
                return UpdateResponsePart(state, a.TurnId, a.PartId, p =>
                {
                    if (p.Value is MarkdownResponsePart m)
                    {
                        m.Content += a.Content;
                    }
                });
            case ChatResponsePartAction a:
                if (state.ActiveTurn is null || state.ActiveTurn.Id != a.TurnId)
                {
                    return ReduceOutcome.NoOp;
                }

                state.ActiveTurn.ResponseParts.Add(a.Part);
                return ReduceOutcome.Applied;
            case ChatTurnCompleteAction a:
                return EndTurn(state, a.TurnId, TurnState.Complete, null, null);
            case ChatTurnCancelledAction a:
                return EndTurn(state, a.TurnId, TurnState.Cancelled, null, null);
            case ChatErrorAction a:
                return EndTurn(state, a.TurnId, TurnState.Error, SessionStatus.Error, a.Error);
            case ChatActivityChangedAction a:
                state.Activity = a.Activity;
                return ReduceOutcome.Applied;
            case ChatToolCallStartAction a:
                if (state.ActiveTurn is null || state.ActiveTurn.Id != a.TurnId)
                {
                    return ReduceOutcome.NoOp;
                }

                state.ActiveTurn.ResponseParts.Add(new ResponsePart(new ToolCallResponsePart
                {
                    Kind = ResponsePartKind.ToolCall,
                    ToolCall = new ToolCallState(new ToolCallStreamingState
                    {
                        Status = ToolCallStatus.Streaming,
                        ToolCallId = a.ToolCallId,
                        ToolName = a.ToolName,
                        DisplayName = a.DisplayName,
                        Contributor = a.Contributor,
                        Meta = a.Meta,
                    }),
                }));
                return ReduceOutcome.Applied;
            case ChatToolCallDeltaAction a:
                return ApplyChatToolCallDelta(state, a);
            case ChatToolCallReadyAction a:
                return WithChatRefresh(state, ApplyChatToolCallReady(state, a));
            case ChatToolCallConfirmedAction a:
                return WithChatRefresh(state, ApplyChatToolCallConfirmed(state, a));
            case ChatToolCallCompleteAction a:
                return WithChatRefresh(state, ApplyChatToolCallComplete(state, a));
            case ChatToolCallResultConfirmedAction a:
                return WithChatRefresh(state, ApplyChatToolCallResultConfirmed(state, a));
            case ChatToolCallContentChangedAction a:
                return UpdateToolCall(state, a.TurnId, a.ToolCallId, tc =>
                {
                    if (tc.Value is ToolCallRunningState r)
                    {
                        if (a.Meta is not null)
                        {
                            r.Meta = a.Meta;
                        }

                        r.Content = CopyList(a.Content)!;
                    }

                    return tc;
                });
            case ChatUsageAction a:
                if (state.ActiveTurn is null || state.ActiveTurn.Id != a.TurnId)
                {
                    return ReduceOutcome.NoOp;
                }

                state.ActiveTurn.Usage = a.Usage;
                return ReduceOutcome.Applied;
            case ChatReasoningAction a:
                return UpdateResponsePart(state, a.TurnId, a.PartId, p =>
                {
                    if (p.Value is ReasoningResponsePart r)
                    {
                        r.Content += a.Content;
                    }
                });
            case ChatTruncatedAction a:
                return ApplyChatTruncated(state, a.TurnId);
            case ChatInputRequestedAction a:
                UpsertChatInputRequest(state, a.Request);
                return ReduceOutcome.Applied;
            case ChatInputAnswerChangedAction a:
                return ApplyChatInputAnswerChanged(state, a);
            case ChatInputCompletedAction a:
                return ApplyChatInputCompleted(state, a);
            case ChatPendingMessageSetAction a:
                return ApplyChatPendingMessageSet(state, a);
            case ChatPendingMessageRemovedAction a:
                return ApplyChatPendingMessageRemoved(state, a);
            case ChatQueuedMessagesReorderedAction a:
                return ApplyChatQueuedMessagesReordered(state, a);
            case ChatDraftChangedAction a:
                state.Draft = a.Draft;
                return ReduceOutcome.Applied;
        }

        return ReduceOutcome.OutOfScope;
    }

    private static ReduceOutcome WithChatRefresh(ChatState state, ReduceOutcome outcome)
    {
        if (outcome == ReduceOutcome.Applied)
        {
            RefreshChatStatus(state);
        }

        return outcome;
    }

    private static ReduceOutcome ApplyChatTurnStarted(ChatState state, ChatTurnStartedAction a)
    {
        state.ActiveTurn = new ActiveTurn
        {
            Id = a.TurnId,
            Message = a.Message,
            ResponseParts = new List<ResponsePart>(),
        };
        state.Status = ChatSummaryStatus(state, null);
        TouchModifiedChat(state);
        state.Status = WithStatusFlag(state.Status, SessionStatus.IsRead, false);

        if (a.QueuedMessageId is { } qmid)
        {
            if (state.SteeringMessage is not null && state.SteeringMessage.Id == qmid)
            {
                state.SteeringMessage = null;
            }

            if (state.QueuedMessages is not null)
            {
                state.QueuedMessages.RemoveAll(m => m.Id == qmid);
                if (state.QueuedMessages.Count == 0)
                {
                    state.QueuedMessages = null;
                }
            }
        }

        return ReduceOutcome.Applied;
    }

    private static ReduceOutcome ApplyChatToolCallDelta(ChatState state, ChatToolCallDeltaAction a)
    {
        return UpdateToolCall(state, a.TurnId, a.ToolCallId, tc =>
        {
            if (tc.Value is not ToolCallStreamingState s)
            {
                return tc;
            }

            string current = s.PartialInput ?? string.Empty;
            s.PartialInput = current + a.Content;
            if (a.Meta is not null)
            {
                s.Meta = a.Meta;
            }

            if (a.InvocationMessage is not null)
            {
                s.InvocationMessage = a.InvocationMessage;
            }

            return tc;
        });
    }

    private static ReduceOutcome ApplyChatToolCallReady(ChatState state, ChatToolCallReadyAction a)
    {
        return UpdateToolCall(state, a.TurnId, a.ToolCallId, tc =>
        {
            ToolCallCommon common = ToolCallMeta(tc);
            if (a.Meta is not null)
            {
                common = common with { Meta = a.Meta };
            }

            if (tc.Value is ToolCallStreamingState or ToolCallRunningState)
            {
                if (a.Confirmed is not null)
                {
                    return new ToolCallState(new ToolCallRunningState
                    {
                        Status = ToolCallStatus.Running,
                        ToolCallId = common.Id,
                        ToolName = common.Name,
                        DisplayName = common.DisplayName,
                        Contributor = common.Contributor,
                        Meta = common.Meta,
                        InvocationMessage = a.InvocationMessage,
                        ToolInput = a.ToolInput,
                        Confirmed = a.Confirmed.Value,
                    });
                }

                return new ToolCallState(new ToolCallPendingConfirmationState
                {
                    Status = ToolCallStatus.PendingConfirmation,
                    ToolCallId = common.Id,
                    ToolName = common.Name,
                    DisplayName = common.DisplayName,
                    Contributor = common.Contributor,
                    Meta = common.Meta,
                    InvocationMessage = a.InvocationMessage,
                    ToolInput = a.ToolInput,
                    ConfirmationTitle = a.ConfirmationTitle,
                    Edits = a.Edits,
                    Editable = a.Editable,
                    Options = a.Options,
                });
            }

            return tc;
        });
    }

    private static ConfirmationOption? ResolveSelectedOption(List<ConfirmationOption>? options, string? id)
    {
        if (id is null || options is null)
        {
            return null;
        }

        foreach (ConfirmationOption opt in options)
        {
            if (opt.Id == id)
            {
                return opt;
            }
        }

        return null;
    }

    private static ReduceOutcome ApplyChatToolCallConfirmed(ChatState state, ChatToolCallConfirmedAction a)
    {
        return UpdateToolCall(state, a.TurnId, a.ToolCallId, tc =>
        {
            if (tc.Value is not ToolCallPendingConfirmationState s)
            {
                return tc;
            }

            ConfirmationOption? selected = ResolveSelectedOption(s.Options, a.SelectedOptionId);
            if (a.Meta is not null)
            {
                s = s with { Meta = a.Meta };
            }

            if (a.Approved)
            {
                string? toolInput = a.EditedToolInput ?? s.ToolInput;
                ToolCallConfirmationReason confirmed = a.Confirmed ?? ToolCallConfirmationReason.NotNeeded;
                return new ToolCallState(new ToolCallRunningState
                {
                    Status = ToolCallStatus.Running,
                    ToolCallId = s.ToolCallId,
                    ToolName = s.ToolName,
                    DisplayName = s.DisplayName,
                    Contributor = s.Contributor,
                    Meta = s.Meta,
                    InvocationMessage = s.InvocationMessage,
                    ToolInput = toolInput,
                    Confirmed = confirmed,
                    SelectedOption = selected,
                });
            }

            ToolCallCancellationReason reason = a.Reason ?? ToolCallCancellationReason.Denied;
            return new ToolCallState(new ToolCallCancelledState
            {
                Status = ToolCallStatus.Cancelled,
                ToolCallId = s.ToolCallId,
                ToolName = s.ToolName,
                DisplayName = s.DisplayName,
                Contributor = s.Contributor,
                Meta = s.Meta,
                InvocationMessage = s.InvocationMessage,
                ToolInput = s.ToolInput,
                Reason = reason,
                ReasonMessage = a.ReasonMessage,
                UserSuggestion = a.UserSuggestion,
                SelectedOption = selected,
            });
        });
    }

    private static ReduceOutcome ApplyChatToolCallComplete(ChatState state, ChatToolCallCompleteAction a)
    {
        return UpdateToolCall(state, a.TurnId, a.ToolCallId, tc =>
        {
            ToolCallCommon common = ToolCallMeta(tc);
            if (a.Meta is not null)
            {
                common = common with { Meta = a.Meta };
            }

            StringOrMarkdown invocation;
            string? toolInput;
            ToolCallConfirmationReason confirmed = ToolCallConfirmationReason.NotNeeded;
            ConfirmationOption? selectedOption = null;

            switch (tc.Value)
            {
                case ToolCallRunningState v:
                    invocation = v.InvocationMessage;
                    toolInput = v.ToolInput;
                    confirmed = v.Confirmed;
                    selectedOption = v.SelectedOption;
                    break;
                case ToolCallPendingConfirmationState v:
                    invocation = v.InvocationMessage;
                    toolInput = v.ToolInput;
                    break;
                default:
                    return tc;
            }

            bool requiresResultConfirmation = a.RequiresResultConfirmation == true;
            if (requiresResultConfirmation)
            {
                return new ToolCallState(new ToolCallPendingResultConfirmationState
                {
                    Status = ToolCallStatus.PendingResultConfirmation,
                    ToolCallId = common.Id,
                    ToolName = common.Name,
                    DisplayName = common.DisplayName,
                    Contributor = common.Contributor,
                    Meta = common.Meta,
                    InvocationMessage = invocation,
                    ToolInput = toolInput,
                    Success = a.Result.Success,
                    PastTenseMessage = a.Result.PastTenseMessage,
                    Content = CopyList(a.Result.Content)!,
                    StructuredContent = a.Result.StructuredContent,
                    Error = a.Result.Error,
                    Confirmed = confirmed,
                    SelectedOption = selectedOption,
                });
            }

            return new ToolCallState(new ToolCallCompletedState
            {
                Status = ToolCallStatus.Completed,
                ToolCallId = common.Id,
                ToolName = common.Name,
                DisplayName = common.DisplayName,
                Contributor = common.Contributor,
                Meta = common.Meta,
                InvocationMessage = invocation,
                ToolInput = toolInput,
                Success = a.Result.Success,
                PastTenseMessage = a.Result.PastTenseMessage,
                Content = CopyList(a.Result.Content)!,
                StructuredContent = a.Result.StructuredContent,
                Error = a.Result.Error,
                Confirmed = confirmed,
                SelectedOption = selectedOption,
            });
        });
    }

    private static ReduceOutcome ApplyChatToolCallResultConfirmed(ChatState state, ChatToolCallResultConfirmedAction a)
    {
        return UpdateToolCall(state, a.TurnId, a.ToolCallId, tc =>
        {
            if (tc.Value is not ToolCallPendingResultConfirmationState s)
            {
                return tc;
            }

            if (a.Meta is not null)
            {
                s = s with { Meta = a.Meta };
            }

            if (a.Approved)
            {
                return new ToolCallState(new ToolCallCompletedState
                {
                    Status = ToolCallStatus.Completed,
                    ToolCallId = s.ToolCallId,
                    ToolName = s.ToolName,
                    DisplayName = s.DisplayName,
                    Contributor = s.Contributor,
                    Meta = s.Meta,
                    InvocationMessage = s.InvocationMessage,
                    ToolInput = s.ToolInput,
                    Success = s.Success,
                    PastTenseMessage = s.PastTenseMessage,
                    Content = s.Content,
                    StructuredContent = s.StructuredContent,
                    Error = s.Error,
                    Confirmed = s.Confirmed,
                    SelectedOption = s.SelectedOption,
                });
            }

            return new ToolCallState(new ToolCallCancelledState
            {
                Status = ToolCallStatus.Cancelled,
                ToolCallId = s.ToolCallId,
                ToolName = s.ToolName,
                DisplayName = s.DisplayName,
                Contributor = s.Contributor,
                Meta = s.Meta,
                InvocationMessage = s.InvocationMessage,
                ToolInput = s.ToolInput,
                Reason = ToolCallCancellationReason.ResultDenied,
                SelectedOption = s.SelectedOption,
            });
        });
    }

    private static ReduceOutcome ApplyChatTruncated(ChatState state, string? turnId)
    {
        if (turnId is null)
        {
            state.Turns = new List<Turn>();
        }
        else
        {
            int idx = state.Turns.FindIndex(t => t.Id == turnId);
            if (idx < 0)
            {
                return ReduceOutcome.NoOp;
            }

            state.Turns = state.Turns.GetRange(0, idx + 1);
        }

        state.ActiveTurn = null;
        state.InputRequests = null;
        TouchModifiedChat(state);
        state.Status = ChatSummaryStatus(state, null);
        return ReduceOutcome.Applied;
    }

    private static ReduceOutcome ApplyChatInputAnswerChanged(ChatState state, ChatInputAnswerChangedAction a)
    {
        List<ChatInputRequest>? list = state.InputRequests;
        int idx = list?.FindIndex(r => r.Id == a.RequestId) ?? -1;
        if (idx < 0 || list is null)
        {
            return ReduceOutcome.NoOp;
        }

        ChatInputRequest req = list[idx];
        req.Answers ??= new Dictionary<string, ChatInputAnswer>();
        if (a.Answer is null)
        {
            req.Answers.Remove(a.QuestionId);
        }
        else
        {
            req.Answers[a.QuestionId] = a.Answer;
        }

        if (req.Answers.Count == 0)
        {
            req.Answers = null;
        }

        TouchModifiedChat(state);
        return ReduceOutcome.Applied;
    }

    private static ReduceOutcome ApplyChatInputCompleted(ChatState state, ChatInputCompletedAction a)
    {
        List<ChatInputRequest>? list = state.InputRequests;
        if (list is null)
        {
            return ReduceOutcome.NoOp;
        }

        int idx = list.FindIndex(r => r.Id == a.RequestId);
        if (idx < 0)
        {
            return ReduceOutcome.NoOp;
        }

        list.RemoveAt(idx);
        state.InputRequests = list.Count == 0 ? null : list;
        RefreshChatStatus(state);
        TouchModifiedChat(state);
        return ReduceOutcome.Applied;
    }

    private static ReduceOutcome ApplyChatPendingMessageSet(ChatState state, ChatPendingMessageSetAction a)
    {
        var entry = new PendingMessage { Id = a.Id, Message = a.Message };
        switch (a.Kind)
        {
            case PendingMessageKind.Steering:
                state.SteeringMessage = entry;
                break;
            case PendingMessageKind.Queued:
                List<PendingMessage> list = state.QueuedMessages ?? new List<PendingMessage>();
                int idx = list.FindIndex(m => m.Id == entry.Id);
                if (idx >= 0)
                {
                    list[idx] = entry;
                }
                else
                {
                    list.Add(entry);
                }

                state.QueuedMessages = list;
                break;
        }

        return ReduceOutcome.Applied;
    }

    private static ReduceOutcome ApplyChatPendingMessageRemoved(ChatState state, ChatPendingMessageRemovedAction a)
    {
        switch (a.Kind)
        {
            case PendingMessageKind.Steering:
                if (state.SteeringMessage is not null && state.SteeringMessage.Id == a.Id)
                {
                    state.SteeringMessage = null;
                    return ReduceOutcome.Applied;
                }

                return ReduceOutcome.NoOp;
            case PendingMessageKind.Queued:
                List<PendingMessage>? list = state.QueuedMessages;
                if (list is null)
                {
                    return ReduceOutcome.NoOp;
                }

                int removed = list.RemoveAll(m => m.Id == a.Id);
                if (removed == 0)
                {
                    return ReduceOutcome.NoOp;
                }

                state.QueuedMessages = list.Count == 0 ? null : list;
                return ReduceOutcome.Applied;
        }

        return ReduceOutcome.NoOp;
    }

    private static ReduceOutcome ApplyChatQueuedMessagesReordered(ChatState state, ChatQueuedMessagesReorderedAction a)
    {
        if (state.QueuedMessages is null)
        {
            return ReduceOutcome.NoOp;
        }

        var byId = new Dictionary<string, PendingMessage>(state.QueuedMessages.Count);
        foreach (PendingMessage m in state.QueuedMessages)
        {
            byId[m.Id] = m;
        }

        var reordered = new List<PendingMessage>(byId.Count);
        var seen = new HashSet<string>();
        foreach (string id in a.Order)
        {
            if (byId.TryGetValue(id, out PendingMessage? msg) && seen.Add(id))
            {
                reordered.Add(msg);
            }
        }

        // Append messages absent from `order`, preserving their original order.
        foreach (PendingMessage m in state.QueuedMessages)
        {
            if (!seen.Contains(m.Id))
            {
                reordered.Add(m);
            }
        }

        state.QueuedMessages = reordered;
        return ReduceOutcome.Applied;
    }

    // ─── Session chat catalog helpers ──────────────────────────────────────

    private static ReduceOutcome ApplySessionChatAdded(SessionState state, SessionChatAddedAction a)
    {
        List<ChatSummary> chats = state.Chats;
        int idx = chats.FindIndex(c => c.Resource == a.Summary.Resource);
        if (idx >= 0)
        {
            chats[idx] = a.Summary;
        }
        else
        {
            chats.Add(a.Summary);
        }

        return ReduceOutcome.Applied;
    }

    private static ReduceOutcome ApplySessionChatRemoved(SessionState state, SessionChatRemovedAction a)
    {
        int idx = state.Chats.FindIndex(c => c.Resource == a.Chat);
        if (idx < 0)
        {
            return ReduceOutcome.NoOp;
        }

        state.Chats.RemoveAt(idx);
        if (state.DefaultChat == a.Chat)
        {
            state.DefaultChat = null;
        }

        return ReduceOutcome.Applied;
    }

    private static ReduceOutcome ApplySessionChatUpdated(SessionState state, SessionChatUpdatedAction a)
    {
        int idx = state.Chats.FindIndex(c => c.Resource == a.Chat);
        if (idx < 0)
        {
            return ReduceOutcome.NoOp;
        }

        ChatSummary s = state.Chats[idx];
        PartialChatSummary ch = a.Changes;
        if (ch.Title is not null) { s.Title = ch.Title; }
        if (ch.Status is not null) { s.Status = ch.Status.Value; }
        if (ch.Activity is not null) { s.Activity = ch.Activity; }
        if (ch.ModifiedAt is not null) { s.ModifiedAt = ch.ModifiedAt; }
        if (ch.Origin is not null) { s.Origin = ch.Origin; }
        if (ch.Interactivity is not null) { s.Interactivity = ch.Interactivity; }
        if (ch.WorkingDirectory is not null) { s.WorkingDirectory = ch.WorkingDirectory; }
        return ReduceOutcome.Applied;
    }

    private static ReduceOutcome ApplyCustomizationUpdated(SessionState state, SessionCustomizationUpdatedAction a)
    {
        if (!TryCustomizationId(a.Customization, out string actionId))
        {
            return ReduceOutcome.NoOp;
        }

        List<Customization> list = state.Customizations ?? new List<Customization>();
        int idx = -1;
        for (int i = 0; i < list.Count; i++)
        {
            if (TryCustomizationId(list[i], out string got) && got == actionId)
            {
                idx = i;
                break;
            }
        }

        if (idx >= 0)
        {
            list[idx] = a.Customization;
        }
        else
        {
            list.Add(a.Customization);
        }

        state.Customizations = list;
        return ReduceOutcome.Applied;
    }

    private static ReduceOutcome ApplyCustomizationRemoved(SessionState state, SessionCustomizationRemovedAction a)
    {
        List<Customization>? list = state.Customizations;
        if (list is null)
        {
            return ReduceOutcome.NoOp;
        }

        for (int i = 0; i < list.Count; i++)
        {
            if (TryCustomizationId(list[i], out string got) && got == a.Id)
            {
                list.RemoveAt(i);
                return ReduceOutcome.Applied;
            }
        }

        foreach (Customization c in list)
        {
            List<ChildCustomization>? children = ContainerChildren(c);
            if (children is null)
            {
                continue;
            }

            for (int j = 0; j < children.Count; j++)
            {
                if (TryChildCustomizationId(children[j], out string childGot) && childGot == a.Id)
                {
                    children.RemoveAt(j);
                    return ReduceOutcome.Applied;
                }
            }
        }

        return ReduceOutcome.NoOp;
    }

    /// <summary>
    /// Applies a <c>session/mcpServerStateChanged</c> action: a
    /// full-replacement of an MCP server customization's
    /// <see cref="McpServerCustomization.State"/> and
    /// <see cref="McpServerCustomization.Channel"/>, located by id.
    ///
    /// Mirrors the canonical TypeScript reducer (and the Go/Rust ports):
    /// a top-level <see cref="McpServerCustomization"/> entry is matched first
    /// (the host MAY surface MCP servers directly at the top level); otherwise
    /// the search descends into container children. The action is a no-op when
    /// no customization carries the id, or when the matched id belongs to a
    /// non-MCP customization type.
    /// </summary>
    private static ReduceOutcome ApplyMcpServerStateChanged(SessionState state, SessionMcpServerStateChangedAction a)
    {
        List<Customization>? list = state.Customizations;
        if (list is null)
        {
            return ReduceOutcome.NoOp;
        }

        // Top-level entries. McpServerCustomization is a valid top-level
        // Customization variant, but it is intentionally absent from the
        // container-id helper (TryCustomizationId only knows the Plugin /
        // Directory containers), so match it directly here.
        foreach (Customization c in list)
        {
            if (c.Value is McpServerCustomization top && top.Id == a.Id)
            {
                top.State = a.State;
                top.Channel = a.Channel;
                return ReduceOutcome.Applied;
            }

            // A non-MCP top-level customization that carries the id is a no-op
            // (the id targets a customization that is not an MCP server).
            if (TryCustomizationId(c, out string topGot) && topGot == a.Id)
            {
                return ReduceOutcome.NoOp;
            }
        }

        // Container children.
        foreach (Customization c in list)
        {
            List<ChildCustomization>? children = ContainerChildren(c);
            if (children is null)
            {
                continue;
            }

            foreach (ChildCustomization child in children)
            {
                if (child.Value is McpServerCustomization mcp && mcp.Id == a.Id)
                {
                    mcp.State = a.State;
                    mcp.Channel = a.Channel;
                    return ReduceOutcome.Applied;
                }

                if (TryChildCustomizationId(child, out string childGot) && childGot == a.Id)
                {
                    // id belongs to a non-MCP child customization → no-op.
                    return ReduceOutcome.NoOp;
                }
            }
        }

        return ReduceOutcome.NoOp;
    }

    // ─── Terminal Reducer ──────────────────────────────────────────────────

    /// <summary>
    /// Applies <paramref name="action"/> to the <see cref="TerminalState"/> in
    /// place. Returns <see cref="ReduceOutcome.OutOfScope"/> for actions that
    /// target a different state tree.
    /// </summary>
    public static ReduceOutcome ApplyToTerminal(TerminalState state, StateAction action)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(action);
        switch (action.Value)
        {
            case TerminalDataAction a:
                AppendTerminalData(state, a.Data);
                return ReduceOutcome.Applied;
            case TerminalInputAction:
                return ReduceOutcome.NoOp;
            case TerminalResizedAction a:
                state.Cols = a.Cols;
                state.Rows = a.Rows;
                return ReduceOutcome.Applied;
            case TerminalClaimedAction a:
                state.Claim = a.Claim;
                return ReduceOutcome.Applied;
            case TerminalTitleChangedAction a:
                state.Title = a.Title;
                return ReduceOutcome.Applied;
            case TerminalCwdChangedAction a:
                state.Cwd = a.Cwd;
                return ReduceOutcome.Applied;
            case TerminalExitedAction a:
                state.ExitCode = a.ExitCode;
                return ReduceOutcome.Applied;
            case TerminalClearedAction:
                state.Content = new List<TerminalContentPart>();
                return ReduceOutcome.Applied;
            case TerminalCommandDetectionAvailableAction:
                state.SupportsCommandDetection = true;
                return ReduceOutcome.Applied;
            case TerminalCommandExecutedAction a:
                state.Content.Add(new TerminalContentPart(new TerminalCommandPart
                {
                    Type = "command",
                    CommandId = a.CommandId,
                    CommandLine = a.CommandLine,
                    // `output` is schema-required; it starts empty and accrues
                    // via terminal/data appends (see AppendTerminalData).
                    Output = "",
                    Timestamp = a.Timestamp,
                    IsComplete = false,
                }));
                state.SupportsCommandDetection = true;
                return ReduceOutcome.Applied;
            case TerminalCommandFinishedAction a:
                foreach (TerminalContentPart part in state.Content)
                {
                    if (part.Value is TerminalCommandPart c && c.CommandId == a.CommandId)
                    {
                        c.IsComplete = true;
                        c.ExitCode = a.ExitCode;
                        c.DurationMs = a.DurationMs;
                        return ReduceOutcome.Applied;
                    }
                }

                return ReduceOutcome.NoOp;
        }

        return ReduceOutcome.OutOfScope;
    }

    private static void AppendTerminalData(TerminalState state, string data)
    {
        int n = state.Content.Count;
        if (n > 0)
        {
            switch (state.Content[n - 1].Value)
            {
                case TerminalCommandPart tail when tail.IsComplete == false:
                    tail.Output = (tail.Output ?? string.Empty) + data;
                    return;
                case TerminalUnclassifiedPart tail:
                    tail.Value += data;
                    return;
            }
        }

        state.Content.Add(new TerminalContentPart(new TerminalUnclassifiedPart
        {
            Type = "unclassified",
            Value = data,
        }));
    }

    // ─── Changeset Reducer ─────────────────────────────────────────────────

    /// <summary>
    /// Applies <paramref name="action"/> to the <see cref="ChangesetState"/> in
    /// place. Faithful port of the canonical TypeScript <c>changesetReducer</c>:
    /// a stable file order is preserved by appending unknown ids and replacing
    /// matching ids in place, and the <c>error</c> payload is carried only while
    /// the relevant status is <c>Error</c> so a recovered changeset or operation
    /// never keeps a stale error. Returns <see cref="ReduceOutcome.OutOfScope"/>
    /// for actions that target a different state tree.
    /// </summary>
    public static ReduceOutcome ApplyToChangeset(ChangesetState state, StateAction action)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(action);
        switch (action.Value)
        {
            case ChangesetStatusChangedAction a:
                // Carry `error` only when the new status is Error so we don't
                // leave a stale error sitting on a recovered changeset.
                state.Status = a.Status;
                state.Error = a.Status == ChangesetStatus.Error ? a.Error : null;
                return ReduceOutcome.Applied;

            case ChangesetFileSetAction a:
                {
                    int idx = state.Files.FindIndex(f => f.Id == a.File.Id);
                    if (idx < 0)
                    {
                        state.Files.Add(a.File);
                    }
                    else
                    {
                        state.Files[idx] = a.File;
                    }

                    return ReduceOutcome.Applied;
                }

            case ChangesetFileRemovedAction a:
                {
                    int idx = state.Files.FindIndex(f => f.Id == a.FileId);
                    if (idx < 0)
                    {
                        return ReduceOutcome.NoOp;
                    }

                    state.Files.RemoveAt(idx);
                    return ReduceOutcome.Applied;
                }

            case ChangesetContentChangedAction a:
                // Full content replacement (snapshots / bulk refreshes): `files`
                // always replaces the previous file list. `operations` replaces
                // the previous list only when present; when omitted (wire
                // `operations` absent) the operation list is left unchanged.
                // `error` is set when present and cleared otherwise, mirroring
                // the canonical TypeScript reducer.
                state.Files = CopyList(a.Files)!;
                if (a.Operations is not null)
                {
                    state.Operations = CopyList(a.Operations);
                }

                state.Error = a.Error;
                return ReduceOutcome.Applied;

            case ChangesetOperationsChangedAction a:
                // Full replacement: a list replaces the previous operations; a
                // null list (wire `operations: null`) clears them entirely.
                state.Operations = a.Operations;
                return ReduceOutcome.Applied;

            case ChangesetOperationStatusChangedAction a:
                {
                    if (state.Operations is null)
                    {
                        return ReduceOutcome.NoOp;
                    }

                    int idx = state.Operations.FindIndex(o => o.Id == a.OperationId);
                    if (idx < 0)
                    {
                        return ReduceOutcome.NoOp;
                    }

                    ChangesetOperation op = state.Operations[idx];
                    // Carry `error` only when the new status is Error so we don't
                    // leave a stale error on an operation that recovered or started
                    // running.
                    op.Status = a.Status;
                    op.Error = a.Status == ChangesetOperationStatus.Error ? a.Error : null;
                    return ReduceOutcome.Applied;
                }

            case ChangesetClearedAction:
                if (state.Files.Count == 0)
                {
                    return ReduceOutcome.NoOp;
                }

                state.Files.Clear();
                return ReduceOutcome.Applied;
        }

        return ReduceOutcome.OutOfScope;
    }

    // ─── Resource-Watch Reducer ────────────────────────────────────────────

    /// <summary>
    /// Applies <paramref name="action"/> to the <see cref="ResourceWatchState"/>
    /// in place. Faithful port of the canonical TypeScript
    /// <c>resourceWatchReducer</c> (and the Kotlin/Rust/Go ports): watches are
    /// intentionally event-pass-through, so <c>resourceWatch/changed</c> leaves
    /// the watch descriptor unchanged (a recognized-but-no-effect
    /// <see cref="ReduceOutcome.NoOp"/>) and the reducer keeps no history of the
    /// delivered changes. Every other action targets a different state tree and
    /// returns <see cref="ReduceOutcome.OutOfScope"/>; both paths leave
    /// <paramref name="state"/> untouched, matching the canonical reducer's
    /// "return state unchanged" for known and unknown actions alike.
    /// </summary>
    public static ReduceOutcome ApplyToResourceWatch(ResourceWatchState state, StateAction action)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(action);
        return action.Value is ResourceWatchChangedAction
            ? ReduceOutcome.NoOp
            : ReduceOutcome.OutOfScope;
    }

    // ─── Annotations Reducer ───────────────────────────────────────────────

    /// <summary>
    /// Applies <paramref name="action"/> to the <see cref="AnnotationsState"/> in
    /// place. Faithful port of the canonical TypeScript <c>annotationsReducer</c>
    /// (and the Kotlin/Rust/Go/Swift ports): the dispatch order of annotations
    /// (and of entries within an annotation) is preserved — new annotations and
    /// entries are appended, a <c>*Set</c> action whose id matches replaces in
    /// place, and an action whose target id is unknown is a no-op (mirroring
    /// <c>changeset/fileRemoved</c> semantics). The single-entry-minimum
    /// invariant is enforced by producers, not the reducer. Returns
    /// <see cref="ReduceOutcome.OutOfScope"/> for actions that target a different
    /// state tree.
    /// </summary>
    public static ReduceOutcome ApplyToAnnotations(AnnotationsState state, StateAction action)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(action);
        switch (action.Value)
        {
            case AnnotationsSetAction a:
                {
                    int idx = state.Annotations.FindIndex(t => t.Id == a.Annotation.Id);
                    if (idx < 0)
                    {
                        state.Annotations.Add(a.Annotation);
                    }
                    else
                    {
                        state.Annotations[idx] = a.Annotation;
                    }

                    return ReduceOutcome.Applied;
                }

            case AnnotationsRemovedAction a:
                {
                    int idx = state.Annotations.FindIndex(t => t.Id == a.AnnotationId);
                    if (idx < 0)
                    {
                        return ReduceOutcome.NoOp;
                    }

                    state.Annotations.RemoveAt(idx);
                    return ReduceOutcome.Applied;
                }

            case AnnotationsEntrySetAction a:
                {
                    int tIdx = state.Annotations.FindIndex(t => t.Id == a.AnnotationId);
                    if (tIdx < 0)
                    {
                        return ReduceOutcome.NoOp;
                    }

                    Annotation annotation = state.Annotations[tIdx];
                    int cIdx = annotation.Entries.FindIndex(c => c.Id == a.Entry.Id);
                    if (cIdx < 0)
                    {
                        annotation.Entries.Add(a.Entry);
                    }
                    else
                    {
                        annotation.Entries[cIdx] = a.Entry;
                    }

                    return ReduceOutcome.Applied;
                }

            case AnnotationsEntryRemovedAction a:
                {
                    int tIdx = state.Annotations.FindIndex(t => t.Id == a.AnnotationId);
                    if (tIdx < 0)
                    {
                        return ReduceOutcome.NoOp;
                    }

                    Annotation annotation = state.Annotations[tIdx];
                    int cIdx = annotation.Entries.FindIndex(c => c.Id == a.EntryId);
                    if (cIdx < 0)
                    {
                        return ReduceOutcome.NoOp;
                    }

                    annotation.Entries.RemoveAt(cIdx);
                    return ReduceOutcome.Applied;
                }

            case AnnotationsUpdatedAction a:
                {
                    int idx = state.Annotations.FindIndex(t => t.Id == a.AnnotationId);
                    if (idx < 0)
                    {
                        return ReduceOutcome.NoOp;
                    }

                    Annotation ann = state.Annotations[idx];
                    state.Annotations[idx] = ann with
                    {
                        TurnId = a.TurnId ?? ann.TurnId,
                        Resource = a.Resource ?? ann.Resource,
                        Range = a.Range ?? ann.Range,
                        Resolved = a.Resolved ?? ann.Resolved,
                    };
                    return ReduceOutcome.Applied;
                }
        }

        return ReduceOutcome.OutOfScope;
    }

    // ─── Client Dispatchable ───────────────────────────────────────────────

    /// <summary>
    /// The set of action wire-<c>type</c> strings a client is allowed to
    /// dispatch. Mirrors the Swift client's <c>clientDispatchableActions</c>
    /// — the cross-language contract for which actions originate on the client
    /// channel rather than host-only.
    /// </summary>
    public static readonly IReadOnlySet<string> ClientDispatchableActions = new HashSet<string>
    {
        // Chat-channel actions (post-#213)
        "chat/turnStarted",
        "chat/toolCallConfirmed",
        "chat/toolCallComplete",
        "chat/toolCallResultConfirmed",
        "chat/turnCancelled",
        "chat/pendingMessageSet",
        "chat/pendingMessageRemoved",
        "chat/queuedMessagesReordered",
        "chat/draftChanged",
        "chat/inputAnswerChanged",
        "chat/inputCompleted",
        // Session-level actions that remain on the session channel
        "session/activeClientSet",
        "session/activeClientRemoved",
        "session/customizationToggled",
        "session/isReadChanged",
        "session/isArchivedChanged",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// Checks whether <paramref name="action"/> may be dispatched by a client.
    /// The action's wire <c>type</c> is read directly from the variant's
    /// <c>Type</c> discriminator (mapped to its wire string via the generated
    /// <c>[WireValue]</c> attributes) and tested for membership in
    /// <see cref="ClientDispatchableActions"/> — without serializing the whole
    /// action graph. An unknown variant carried as a raw <see cref="JsonElement"/>
    /// reads its <c>type</c> field directly. Mirrors the Swift client's
    /// <c>isClientDispatchable</c>.
    /// </summary>
    // Reads the variant's `Type` property reflectively + the [WireValue] attributes
    // on ActionType — trim/AOT-relevant, so the declaration is kept; the cost is one
    // cached property read, not a full serialize of the action's nested payload.
    [RequiresUnreferencedCode("Reflects over the action variant's Type property and ActionType's [WireValue] members; trimming may remove the metadata it reads. Declared (not suppressed) so trim/AOT consumers are warned at the call site.")]
    [RequiresDynamicCode("Reflects over the action variant's Type property and ActionType's [WireValue] members.")]
    public static bool IsClientDispatchable(StateAction action)
    {
        ArgumentNullException.ThrowIfNull(action);

        var inner = action.Value;
        switch (inner)
        {
            case null:
                return false;
            // Unknown variant preserved as raw JSON: read its `type` field directly.
            case JsonElement el:
                return el.ValueKind == JsonValueKind.Object
                    && el.TryGetProperty("type", out var t)
                    && t.ValueKind == JsonValueKind.String
                    && t.GetString() is { } raw
                    && ClientDispatchableActions.Contains(raw);
            default:
                // Known variant record: read its ActionType discriminator and map it
                // to the wire string the serializer would have emitted.
                if (TryReadActionType(inner, out var actionType)
                    && s_actionTypeWire.TryGetValue(actionType, out var wire))
                {
                    return ClientDispatchableActions.Contains(wire);
                }
                return false;
        }
    }

    // Cache: variant CLR type -> its `Type` (ActionType) property accessor. Every
    // generated state-action variant carries `public ActionType Type { get; init; }`.
    private static readonly ConcurrentDictionary<Type, PropertyInfo?> s_typeProperty = new();

    // ActionType -> wire string, derived once from the [WireValue] attributes (the
    // same source the WireEnumConverter uses), so the lookup needs no serialize.
    private static readonly Dictionary<ActionType, string> s_actionTypeWire = BuildActionTypeWireMap();

    [UnconditionalSuppressMessage("Trimming", "IL2070",
        Justification = "GetProperty(\"Type\") over a state-action variant CLR type; the variants are all preserved generated records with a public ActionType Type property.")]
    private static bool TryReadActionType(object variant, out ActionType actionType)
    {
        var prop = s_typeProperty.GetOrAdd(variant.GetType(), static t => t.GetProperty("Type"));
        if (prop is not null && prop.GetValue(variant) is ActionType at)
        {
            actionType = at;
            return true;
        }
        actionType = default;
        return false;
    }

    private static Dictionary<ActionType, string> BuildActionTypeWireMap()
    {
        var map = new Dictionary<ActionType, string>();
        foreach (FieldInfo field in typeof(ActionType).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            var value = (ActionType)field.GetValue(null)!;
            map[value] = field.GetCustomAttribute<WireValueAttribute>()?.Value ?? field.Name;
        }
        return map;
    }
}
