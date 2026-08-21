#![allow(clippy::panic, clippy::unwrap_used)]

use ahp_types::{
    commands::ChatSource,
    state::{ResponsePart, ResponsePartKind},
};

#[test]
fn chat_source_routes_by_kind() {
    let fork = serde_json::from_str::<ChatSource>(
        r#"{"kind":"fork","chat":"ahp-chat:/main","turnId":"turn-12"}"#,
    )
    .expect("decode fork source");
    let side_chat = serde_json::from_str::<ChatSource>(
        r#"{"kind":"sideChat","chat":"ahp-chat:/main","turnId":"turn-active","selection":{"text":"const value = compute()","responsePartId":"part-7"}}"#,
    )
    .expect("decode side chat source");

    match fork {
        ChatSource::Fork(value) => {
            assert_eq!(value.chat, "ahp-chat:/main");
            assert_eq!(value.turn_id, "turn-12");
        }
        other => panic!("expected fork variant, got {other:?}"),
    }

    match side_chat {
        ChatSource::SideChat(value) => {
            assert_eq!(value.chat, "ahp-chat:/main");
            assert_eq!(value.turn_id, "turn-active");
            let selection = value.selection.expect("selection should decode");
            assert_eq!(selection.text, "const value = compute()");
            assert_eq!(selection.response_part_id.as_deref(), Some("part-7"));
        }
        other => panic!("expected sideChat variant, got {other:?}"),
    }
}

#[test]
fn chat_source_preserves_missing_or_unknown_kind() {
    for raw in [
        r#"{"chat":"ahp-chat:/main","turnId":"turn-12"}"#,
        r#"{"kind":"future","chat":"ahp-chat:/main","turnId":"turn-12"}"#,
    ] {
        let source = serde_json::from_str::<ChatSource>(raw).expect("decode unknown chat source");
        assert!(matches!(source, ChatSource::Unknown(_)));
        assert_eq!(
            serde_json::to_value(&source).expect("encode unknown chat source"),
            serde_json::from_str::<serde_json::Value>(raw).expect("decode expected JSON")
        );
    }
}

#[test]
fn nonexhaustive_enum_and_union_preserve_future_values() {
    let kind = serde_json::from_str::<ResponsePartKind>(r#""futurePart""#)
        .expect("decode unknown response-part kind");
    assert!(matches!(&kind, ResponsePartKind::Unknown(value) if value == "futurePart"));
    assert_eq!(
        serde_json::to_string(&kind).expect("encode unknown response-part kind"),
        r#""futurePart""#
    );

    let raw = r#"{"kind":"futurePart","payload":{"preserve":true}}"#;
    let part = serde_json::from_str::<ResponsePart>(raw).expect("decode unknown response part");
    assert!(matches!(part, ResponsePart::Unknown(_)));
    assert_eq!(
        serde_json::to_string(&part).expect("encode unknown response part"),
        raw
    );
}
