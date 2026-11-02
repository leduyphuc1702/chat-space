package main

import (
	"context"
	"testing"

	"go.mau.fi/mautrix-meta/pkg/messagix/table"
)

// Encrypted threads have no LightSpeed history; listing one must answer from the
// store without queueing a server page (which only ever timed out).
func TestListMessagesEncryptedSkipsServerPaging(t *testing.T) {
	h := newHarness(t)
	b := h.b
	b.state = stateLoggedIn
	b.self = &SelfInfo{ID: "1", Name: "Me"}
	const key = int64(7000000000000001)

	b.ingestTable(&table.LSTable{
		LSDeleteThenInsertThread: []*table.LSDeleteThenInsertThread{
			{ThreadKey: key, ThreadType: table.ENCRYPTED_OVER_WA_ONE_TO_ONE, LastActivityTimestampMs: 10},
		},
	}, modeInitial)
	chat := chatKey(key)
	b.st.putMessage(&storedMsg{Message: Message{ID: "m1", ChatID: chat, Kind: "text", Text: "hi", Timestamp: 5}, Sort: 5})

	msgs, err := b.listMessages(context.Background(), chat, 50, "")
	if err != nil {
		t.Fatal(err)
	}
	if len(msgs) != 1 || msgs[0].ID != "m1" {
		t.Fatalf("messages = %+v, want the stored one", msgs)
	}
	b.st.mu.Lock()
	deep := b.st.deep[chat]
	b.st.mu.Unlock()
	if deep {
		t.Fatal("encrypted thread was queued for server paging")
	}
	th, _ := b.st.thread(chat)
	if !b.chatView(th).Encrypted {
		t.Fatal("chat view of an encrypted thread is not marked encrypted")
	}
}

// Messages of the pre-encryption thread (keyed by the peer's user id) belong to the
// encrypted chat that replaced it; the old thread must not become a second chat.
func TestOpenThreadMessagesFiledUnderEncryptedChat(t *testing.T) {
	h := newHarness(t)
	b := h.b
	b.state = stateLoggedIn
	b.self = &SelfInfo{ID: "1", Name: "Me"}
	const key, peer = int64(7000000000000001), int64(100000000000001)
	chat := chatKey(key)

	b.ingestTable(&table.LSTable{
		LSVerifyHybridThreadExists: []*table.LSVerifyHybridThreadExists{{ThreadKey: key, ThreadJID: peer}},
		LSDeleteThenInsertThread: []*table.LSDeleteThenInsertThread{
			{ThreadKey: key, ThreadType: table.ENCRYPTED_OVER_WA_ONE_TO_ONE, LastActivityTimestampMs: 900},
		},
	}, modeInitial)
	b.st.putMessage(&storedMsg{Message: Message{ID: "e2ee-1", ChatID: chat, Kind: "text", Text: "new", Timestamp: 900}, SenderJID: "100000000000001@msgr", Sort: 900})

	b.ingestTable(&table.LSTable{
		LSDeleteThenInsertThread: []*table.LSDeleteThenInsertThread{
			{ThreadKey: peer, ThreadType: table.ONE_TO_ONE, FolderName: "e2ee_cutover", LastActivityTimestampMs: 200},
		},
		LSInsertNewMessageRange: []*table.LSInsertNewMessageRange{
			{ThreadKey: peer, MinTimestampMs: 100, MaxTimestampMs: 200, HasMoreBefore: false},
		},
		LSUpsertMessage: []*table.LSUpsertMessage{
			{ThreadKey: peer, MessageId: "mid.$old1", TimestampMs: 100, Text: "first", SenderId: peer},
			{ThreadKey: peer, MessageId: "mid.$old2", TimestampMs: 200, Text: "second", SenderId: 1},
		},
	}, modeInitial)

	if _, ok := b.st.thread(chatKey(peer)); ok {
		t.Fatal("the pre-encryption thread became a chat of its own")
	}
	msgs, _ := b.st.listMessages(chat, "", 10)
	var ids []string
	for _, m := range msgs {
		ids = append(ids, m.ID)
	}
	if len(ids) != 3 || ids[0] != "mid.$old1" || ids[1] != "mid.$old2" || ids[2] != "e2ee-1" {
		t.Fatalf("messages = %v, want the two old ones before the encrypted one", ids)
	}
	th, _ := b.st.thread(chat)
	if !th.OpenNoMoreBefore || th.NoMoreBefore {
		t.Fatalf("OpenNoMoreBefore=%v NoMoreBefore=%v, want true/false", th.OpenNoMoreBefore, th.NoMoreBefore)
	}
	if th.LastActivity != 900 || b.chatView(th).LastMessage.ID != "e2ee-1" {
		t.Fatalf("old messages changed the chat's preview: %+v", b.chatView(th).LastMessage)
	}
	if id, _, _ := b.st.oldestOpen(chat); id != "mid.$old1" {
		t.Fatalf("oldestOpen = %q, want mid.$old1", id)
	}
	if got := b.chatView(th).EncryptedAfter; got != "mid.$old2" {
		t.Fatalf("encryptedAfter = %q, want mid.$old2", got)
	}

	// The backup is paged from the oldest encrypted message, not from the far older
	// pre-encryption ones, or the cutover-to-device gap would never be fetched.
	if got := b.archiveFrontier(chat); got != 900 {
		t.Fatalf("archiveFrontier = %d, want 900", got)
	}

	// Without any encrypted message, a pre-encryption message is not the chat's latest.
	b.st.removeMessage(chat, "e2ee-1")
	th, _ = b.st.thread(chat)
	if lm := b.chatView(th).LastMessage; lm != nil {
		t.Fatalf("preview = %+v, want none (the encrypted latest message is missing)", lm)
	}
}
