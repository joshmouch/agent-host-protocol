// Pure state reducers — a faithful port of the Go client's reducers.go,
// which in turn mirrors the canonical TypeScript reducers. Each reducer
// mutates the supplied state in place and reports whether it applied.
#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
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

    private static readonly Gate s_nowLock = new();
    private static Func<long> s_now = () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>
    /// Overrides the function reducers call to stamp <c>summary.modifiedAt</c>.
    /// Useful for tests that need deterministic output. Pass <see langword="null"/>
    /// to restore the default (current Unix time in milliseconds).
    /// </summary>
    public static void SetNowProvider(Func<long>? provider)
    {
        lock (s_nowLock)
        {
            s_now = provider ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }
    }

    private static long NowMs()
    {
        lock (s_nowLock)
        {
            return s_now();
        }
    }

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
        string? ToolClientId,
        Dictionary<string, JsonElement>? Meta);

    private static ToolCallCommon ToolCallMeta(ToolCallState tc) => tc.Value switch
    {
        ToolCallStreamingState v => new(v.ToolCallId, v.ToolName, v.DisplayName, v.ToolClientId, v.Meta),
        ToolCallPendingConfirmationState v => new(v.ToolCallId, v.ToolName, v.DisplayName, v.ToolClientId, v.Meta),
        ToolCallRunningState v => new(v.ToolCallId, v.ToolName, v.DisplayName, v.ToolClientId, v.Meta),
        ToolCallPendingResultConfirmationState v => new(v.ToolCallId, v.ToolName, v.DisplayName, v.ToolClientId, v.Meta),
        ToolCallCompletedState v => new(v.ToolCallId, v.ToolName, v.DisplayName, v.ToolClientId, v.Meta),
        ToolCallCancelledState v => new(v.ToolCallId, v.ToolName, v.DisplayName, v.ToolClientId, v.Meta),
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

    private static bool HasPendingToolCallConfirmation(SessionState state)
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

    private static SessionStatus SummaryStatus(SessionState state, SessionStatus? terminal)
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

        return (state.Summary.Status & ~StatusActivityMask) | activity;
    }

    private static void RefreshSummaryStatus(SessionState state) =>
        state.Summary.Status = SummaryStatus(state, null);

    private static void TouchModified(SessionState state) =>
        state.Summary.ModifiedAt = NowMs();

    // ─── Active-turn helpers ───────────────────────────────────────────────

    private static ReduceOutcome EndTurn(
        SessionState state,
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
                ToolClientId = common.ToolClientId,
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
        TouchModified(state);
        state.Summary.Status = SummaryStatus(state, terminalStatus);
        return ReduceOutcome.Applied;
    }

    private static void UpsertInputRequest(SessionState state, SessionInputRequest req)
    {
        List<SessionInputRequest> existing = state.InputRequests ?? new List<SessionInputRequest>();
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
        state.Summary.Status = SummaryStatus(state, null);
        TouchModified(state);
        state.Summary.Status = WithStatusFlag(state.Summary.Status, SessionStatus.IsRead, false);
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
        SessionState state,
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
        SessionState state,
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
        switch (action.Value)
        {
            case SessionReadyAction:
                state.Lifecycle = SessionLifecycle.Ready;
                return ReduceOutcome.Applied;
            case SessionCreationFailedAction a:
                state.Lifecycle = SessionLifecycle.CreationFailed;
                state.CreationError = a.Error;
                return ReduceOutcome.Applied;
            case SessionTurnStartedAction a:
                return ApplyTurnStarted(state, a);
            case SessionDeltaAction a:
                return UpdateResponsePart(state, a.TurnId, a.PartId, p =>
                {
                    if (p.Value is MarkdownResponsePart m)
                    {
                        m.Content += a.Content;
                    }
                });
            case SessionResponsePartAction a:
                if (state.ActiveTurn is null || state.ActiveTurn.Id != a.TurnId)
                {
                    return ReduceOutcome.NoOp;
                }

                state.ActiveTurn.ResponseParts.Add(a.Part);
                return ReduceOutcome.Applied;
            case SessionTurnCompleteAction a:
                return EndTurn(state, a.TurnId, TurnState.Complete, null, null);
            case SessionTurnCancelledAction a:
                return EndTurn(state, a.TurnId, TurnState.Cancelled, null, null);
            case SessionErrorAction a:
                return EndTurn(state, a.TurnId, TurnState.Error, SessionStatus.Error, a.Error);
            case SessionToolCallStartAction a:
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
                        ToolClientId = a.ToolClientId,
                        Meta = a.Meta,
                    }),
                }));
                return ReduceOutcome.Applied;
            case SessionToolCallDeltaAction a:
                return ApplyToolCallDelta(state, a);
            case SessionToolCallReadyAction a:
                return WithRefresh(state, ApplyToolCallReady(state, a));
            case SessionToolCallConfirmedAction a:
                return WithRefresh(state, ApplyToolCallConfirmed(state, a));
            case SessionToolCallCompleteAction a:
                return WithRefresh(state, ApplyToolCallComplete(state, a));
            case SessionToolCallResultConfirmedAction a:
                return WithRefresh(state, ApplyToolCallResultConfirmed(state, a));
            case SessionToolCallContentChangedAction a:
                return UpdateToolCall(state, a.TurnId, a.ToolCallId, tc =>
                {
                    if (tc.Value is ToolCallRunningState r)
                    {
                        r.Content = CopyList(a.Content)!;
                    }

                    return tc;
                });
            case SessionTitleChangedAction a:
                state.Summary.Title = a.Title;
                TouchModified(state);
                return ReduceOutcome.Applied;
            case SessionUsageAction a:
                if (state.ActiveTurn is null || state.ActiveTurn.Id != a.TurnId)
                {
                    return ReduceOutcome.NoOp;
                }

                state.ActiveTurn.Usage = a.Usage;
                return ReduceOutcome.Applied;
            case SessionReasoningAction a:
                return UpdateResponsePart(state, a.TurnId, a.PartId, p =>
                {
                    if (p.Value is ReasoningResponsePart r)
                    {
                        r.Content += a.Content;
                    }
                });
            case SessionModelChangedAction a:
                state.Summary.Model = a.Model;
                TouchModified(state);
                return ReduceOutcome.Applied;
            case SessionAgentChangedAction a:
                state.Summary.Agent = a.Agent;
                TouchModified(state);
                return ReduceOutcome.Applied;
            case SessionIsReadChangedAction a:
                state.Summary.Status = WithStatusFlag(state.Summary.Status, SessionStatus.IsRead, a.IsRead);
                return ReduceOutcome.Applied;
            case SessionIsArchivedChangedAction a:
                state.Summary.Status = WithStatusFlag(state.Summary.Status, SessionStatus.IsArchived, a.IsArchived);
                return ReduceOutcome.Applied;
            case SessionActivityChangedAction a:
                state.Summary.Activity = a.Activity;
                return ReduceOutcome.Applied;
            case SessionChangesetsChangedAction a:
                state.Summary.Changesets = CopyList(a.Changesets);
                return ReduceOutcome.Applied;
            case SessionConfigChangedAction a:
                if (state.Config is null)
                {
                    return ReduceOutcome.NoOp;
                }

                state.Config.Values = MergeConfig(state.Config.Values, a.Config, a.Replace);
                TouchModified(state);
                return ReduceOutcome.Applied;
            case SessionMetaChangedAction a:
                state.Meta = a.Meta;
                return ReduceOutcome.Applied;
            case SessionServerToolsChangedAction a:
                state.ServerTools = CopyList(a.Tools)!;
                return ReduceOutcome.Applied;
            case SessionActiveClientChangedAction a:
                state.ActiveClient = a.ActiveClient;
                return ReduceOutcome.Applied;
            case SessionActiveClientToolsChangedAction a:
                if (state.ActiveClient is null)
                {
                    return ReduceOutcome.NoOp;
                }

                state.ActiveClient.Tools = CopyList(a.Tools)!;
                return ReduceOutcome.Applied;
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
            case SessionTruncatedAction a:
                return ApplyTruncated(state, a.TurnId);
            case SessionInputRequestedAction a:
                UpsertInputRequest(state, a.Request);
                return ReduceOutcome.Applied;
            case SessionInputAnswerChangedAction a:
                return ApplyInputAnswerChanged(state, a);
            case SessionInputCompletedAction a:
                return ApplyInputCompleted(state, a);
            case SessionPendingMessageSetAction a:
                return ApplyPendingMessageSet(state, a);
            case SessionPendingMessageRemovedAction a:
                return ApplyPendingMessageRemoved(state, a);
            case SessionQueuedMessagesReorderedAction a:
                return ApplyQueuedMessagesReordered(state, a);
        }

        return ReduceOutcome.OutOfScope;
    }

    private static ReduceOutcome WithRefresh(SessionState state, ReduceOutcome outcome)
    {
        if (outcome == ReduceOutcome.Applied)
        {
            RefreshSummaryStatus(state);
        }

        return outcome;
    }

    private static ReduceOutcome ApplyTurnStarted(SessionState state, SessionTurnStartedAction a)
    {
        state.ActiveTurn = new ActiveTurn
        {
            Id = a.TurnId,
            Message = a.Message,
            ResponseParts = new List<ResponsePart>(),
        };
        state.Summary.Status = SummaryStatus(state, null);
        TouchModified(state);
        state.Summary.Status = WithStatusFlag(state.Summary.Status, SessionStatus.IsRead, false);

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

    private static ReduceOutcome ApplyToolCallDelta(SessionState state, SessionToolCallDeltaAction a)
    {
        return UpdateToolCall(state, a.TurnId, a.ToolCallId, tc =>
        {
            if (tc.Value is not ToolCallStreamingState s)
            {
                return tc;
            }

            string current = s.PartialInput ?? string.Empty;
            s.PartialInput = current + a.Content;
            if (a.InvocationMessage is not null)
            {
                s.InvocationMessage = a.InvocationMessage;
            }

            return tc;
        });
    }

    private static ReduceOutcome ApplyToolCallReady(SessionState state, SessionToolCallReadyAction a)
    {
        return UpdateToolCall(state, a.TurnId, a.ToolCallId, tc =>
        {
            ToolCallCommon common = ToolCallMeta(tc);
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
                        ToolClientId = common.ToolClientId,
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
                    ToolClientId = common.ToolClientId,
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

    private static ReduceOutcome ApplyToolCallConfirmed(SessionState state, SessionToolCallConfirmedAction a)
    {
        return UpdateToolCall(state, a.TurnId, a.ToolCallId, tc =>
        {
            if (tc.Value is not ToolCallPendingConfirmationState s)
            {
                return tc;
            }

            ConfirmationOption? selected = ResolveSelectedOption(s.Options, a.SelectedOptionId);
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
                    ToolClientId = s.ToolClientId,
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
                ToolClientId = s.ToolClientId,
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

    private static ReduceOutcome ApplyToolCallComplete(SessionState state, SessionToolCallCompleteAction a)
    {
        return UpdateToolCall(state, a.TurnId, a.ToolCallId, tc =>
        {
            ToolCallCommon common = ToolCallMeta(tc);
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
                    ToolClientId = common.ToolClientId,
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
                ToolClientId = common.ToolClientId,
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

    private static ReduceOutcome ApplyToolCallResultConfirmed(SessionState state, SessionToolCallResultConfirmedAction a)
    {
        return UpdateToolCall(state, a.TurnId, a.ToolCallId, tc =>
        {
            if (tc.Value is not ToolCallPendingResultConfirmationState s)
            {
                return tc;
            }

            if (a.Approved)
            {
                return new ToolCallState(new ToolCallCompletedState
                {
                    Status = ToolCallStatus.Completed,
                    ToolCallId = s.ToolCallId,
                    ToolName = s.ToolName,
                    DisplayName = s.DisplayName,
                    ToolClientId = s.ToolClientId,
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
                ToolClientId = s.ToolClientId,
                Meta = s.Meta,
                InvocationMessage = s.InvocationMessage,
                ToolInput = s.ToolInput,
                Reason = ToolCallCancellationReason.ResultDenied,
                SelectedOption = s.SelectedOption,
            });
        });
    }

    private static ReduceOutcome ApplyTruncated(SessionState state, string? turnId)
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
        TouchModified(state);
        state.Summary.Status = SummaryStatus(state, null);
        return ReduceOutcome.Applied;
    }

    private static ReduceOutcome ApplyInputAnswerChanged(SessionState state, SessionInputAnswerChangedAction a)
    {
        List<SessionInputRequest>? list = state.InputRequests;
        int idx = list?.FindIndex(r => r.Id == a.RequestId) ?? -1;
        if (idx < 0 || list is null)
        {
            return ReduceOutcome.NoOp;
        }

        SessionInputRequest req = list[idx];
        req.Answers ??= new Dictionary<string, SessionInputAnswer>();
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

        TouchModified(state);
        return ReduceOutcome.Applied;
    }

    private static ReduceOutcome ApplyInputCompleted(SessionState state, SessionInputCompletedAction a)
    {
        List<SessionInputRequest>? list = state.InputRequests;
        if (list is null)
        {
            return ReduceOutcome.NoOp;
        }

        int before = list.Count;
        var next = list.Where(r => r.Id != a.RequestId).ToList();
        if (next.Count == before)
        {
            return ReduceOutcome.NoOp;
        }

        state.InputRequests = next.Count == 0 ? null : next;
        RefreshSummaryStatus(state);
        TouchModified(state);
        return ReduceOutcome.Applied;
    }

    private static ReduceOutcome ApplyPendingMessageSet(SessionState state, SessionPendingMessageSetAction a)
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

    private static ReduceOutcome ApplyPendingMessageRemoved(SessionState state, SessionPendingMessageRemovedAction a)
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

    private static ReduceOutcome ApplyQueuedMessagesReordered(SessionState state, SessionQueuedMessagesReorderedAction a)
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

    // ─── Terminal Reducer ──────────────────────────────────────────────────

    /// <summary>
    /// Applies <paramref name="action"/> to the <see cref="TerminalState"/> in
    /// place. Returns <see cref="ReduceOutcome.OutOfScope"/> for actions that
    /// target a different state tree.
    /// </summary>
    public static ReduceOutcome ApplyToTerminal(TerminalState state, StateAction action)
    {
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
    /// Entry point for changeset actions. Mirrors the Rust/Go clients' stub:
    /// every recognized changeset action short-circuits as
    /// <see cref="ReduceOutcome.NoOp"/> until the full changeset reducer is
    /// ported. Unrelated actions return <see cref="ReduceOutcome.OutOfScope"/>.
    /// </summary>
    public static ReduceOutcome ApplyToChangeset(ChangesetState state, StateAction action)
    {
        _ = state;
        switch (action.Value)
        {
            case ChangesetStatusChangedAction:
            case ChangesetFileSetAction:
            case ChangesetFileRemovedAction:
            case ChangesetOperationsChangedAction:
            case ChangesetClearedAction:
                return ReduceOutcome.NoOp;
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
        "session/turnStarted",
        "session/toolCallConfirmed",
        "session/toolCallComplete",
        "session/toolCallResultConfirmed",
        "session/turnCancelled",
        "session/modelChanged",
        "session/activeClientChanged",
        "session/activeClientToolsChanged",
        "session/pendingMessageSet",
        "session/pendingMessageRemoved",
        "session/queuedMessagesReordered",
        "session/inputAnswerChanged",
        "session/inputCompleted",
        "session/customizationToggled",
        "session/isReadChanged",
        "session/isArchivedChanged",
    };

    /// <summary>
    /// Checks whether <paramref name="action"/> may be dispatched by a client.
    /// The action's wire <c>type</c> is read by serializing it through the real
    /// serializer (there is no public accessor for the generated <c>[WireValue]</c>
    /// mapping), then tested for membership in <see cref="ClientDispatchableActions"/>.
    /// Mirrors the Swift client's <c>isClientDispatchable</c>.
    /// </summary>
    public static bool IsClientDispatchable(StateAction action)
    {
        using var doc = JsonDocument.Parse(SystemTextJsonAhpSerializer.Default.Serialize(action));
        string? type = doc.RootElement.GetProperty("type").GetString();
        return type is not null && ClientDispatchableActions.Contains(type);
    }
}
