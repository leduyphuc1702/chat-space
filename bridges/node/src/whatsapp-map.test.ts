import { describe, expect, test } from "bun:test";
import { proto } from "baileys";
import { association, isStorable, mapMessage, pinEvent, resolveMentions, type MapCtx } from "./whatsapp-map.ts";
import { plainMsg } from "./whatsapp-store.ts";

const ctx: MapCtx = {
  selfId: "84900@s.whatsapp.net",
  selfName: "Me",
  canon: (j) => j ?? "",
  nameOf: (j) => (j === "84901@s.whatsapp.net" ? "Linh" : undefined),
  groupSize: () => undefined,
};

const TS = 1_800_000_000; // s
const target = { remoteJid: "g@g.us", fromMe: false, id: "TARGET", participant: "84901@s.whatsapp.net" };

const pinMsg = (type: number, opts: { secs?: number; fromMe?: boolean; wrap?: boolean } = {}) => {
  const inner: any = { pinInChatMessage: { key: target, type, senderTimestampMs: (TS + 5) * 1000 } };
  if (opts.secs) inner.messageContextInfo = { messageAddOnDurationInSecs: opts.secs };
  return {
    key: { remoteJid: "g@g.us", fromMe: !!opts.fromMe, id: "PIN1", participant: opts.fromMe ? undefined : "84901@s.whatsapp.net" },
    messageTimestamp: TS + 6,
    message: opts.wrap ? { deviceSentMessage: { destinationJid: "g@g.us", message: inner } } : inner,
  };
};

describe("pinEvent", () => {
  test("a pin takes its time from the sender and its duration from the context info", () => {
    expect(pinEvent(pinMsg(1, { secs: 7 * 86400 }))).toEqual({
      id: "TARGET",
      pin: true,
      at: (TS + 5) * 1000,
      expiresAt: (TS + 5 + 7 * 86400) * 1000,
    });
  });

  test("24 hours and 30 days", () => {
    expect(pinEvent(pinMsg(1, { secs: 86400 }))!.expiresAt).toBe((TS + 5 + 86400) * 1000);
    expect(pinEvent(pinMsg(1, { secs: 30 * 86400 }))!.expiresAt).toBe((TS + 5 + 30 * 86400) * 1000);
  });

  test("a pin without a duration lasts 7 days", () => {
    expect(pinEvent(pinMsg(1))!.expiresAt).toBe((TS + 5 + 7 * 86400) * 1000);
  });

  test("an unpin", () => {
    expect(pinEvent(pinMsg(2))).toEqual({ id: "TARGET", pin: false, at: (TS + 5) * 1000, expiresAt: 0 });
  });

  test("protobuf longs and the device-sent wrapper of history are understood", () => {
    const m: any = pinMsg(1, { secs: 86400, fromMe: true, wrap: true });
    m.message.deviceSentMessage.message.pinInChatMessage.senderTimestampMs = { low: (TS + 5) * 1000, high: 0, toNumber: () => (TS + 5) * 1000 };
    expect(pinEvent(m)).toMatchObject({ id: "TARGET", pin: true, at: (TS + 5) * 1000 });
  });

  test("pinInChat attached to the pinned message (history sync) counts too, with its own id as default", () => {
    const raw = {
      key: { remoteJid: "g@g.us", fromMe: false, id: "TARGET" },
      messageTimestamp: TS,
      message: { conversation: "hello" },
      pinInChat: { type: 1, senderTimestampMs: (TS + 9) * 1000, messageAddOnContextInfo: { messageAddOnDurationInSecs: 86400 } },
    };
    expect(pinEvent(raw)).toEqual({ id: "TARGET", pin: true, at: (TS + 9) * 1000, expiresAt: (TS + 9 + 86400) * 1000 });
  });

  test("ordinary messages and unknown pin types are not pin events", () => {
    expect(pinEvent({ key: { id: "X" }, messageTimestamp: TS, message: { conversation: "hi" } })).toBeNull();
    expect(pinEvent(pinMsg(0))).toBeNull();
    expect(pinEvent({ key: { id: "X" }, messageTimestamp: TS })).toBeNull();
  });
});

describe("pin messages as Baileys delivers them", () => {
  // The wire form: protobuf-decoded instances (Long timestamps, numeric enums), then the plain copy kept in the store.
  const decoded = (type: number, secs: number) =>
    proto.WebMessageInfo.decode(
      proto.WebMessageInfo.encode({
        key: { remoteJid: "g@g.us", fromMe: false, id: "PIN2", participant: "84901@s.whatsapp.net" },
        messageTimestamp: TS + 6,
        message: {
          pinInChatMessage: { key: target, type, senderTimestampMs: (TS + 5) * 1000 },
          messageContextInfo: { messageAddOnDurationInSecs: secs },
        },
      }).finish(),
    );

  test("the same event comes out of the live message and of its stored copy", () => {
    const live = decoded(proto.Message.PinInChatMessage.Type.PIN_FOR_ALL, 30 * 86400);
    const want = { id: "TARGET", pin: true, at: (TS + 5) * 1000, expiresAt: (TS + 5 + 30 * 86400) * 1000 };
    expect(pinEvent(live)).toEqual(want);
    const stored = JSON.parse(JSON.stringify(plainMsg(live)));
    expect(pinEvent(stored)).toEqual(want);
    expect(mapMessage({ raw: stored }, "g@g.us", ctx)).toMatchObject({ kind: "system", text: "Linh pinned a message" });
  });

  test("unpin", () => {
    const live = decoded(proto.Message.PinInChatMessage.Type.UNPIN_FOR_ALL, 0);
    expect(pinEvent(live)).toMatchObject({ id: "TARGET", pin: false });
    expect(isStorable(live)).toBe(false);
  });
});

describe("pin messages in the chat", () => {
  test("a pin shows as a system line naming who pinned it", () => {
    const m = mapMessage({ raw: pinMsg(1, { secs: 86400 }) }, "g@g.us", ctx)!;
    expect(m).toMatchObject({ id: "PIN1", kind: "system", text: "Linh pinned a message", fromMe: false });
  });

  test("your own pin, also from the device-sent wrapper", () => {
    expect(mapMessage({ raw: pinMsg(1, { fromMe: true }) }, "g@g.us", ctx)).toMatchObject({ kind: "system", text: "You pinned a message" });
    expect(mapMessage({ raw: pinMsg(1, { fromMe: true, wrap: true }) }, "g@g.us", ctx)).toMatchObject({ kind: "system", text: "You pinned a message" });
  });

  test("an unpin leaves no message", () => {
    expect(mapMessage({ raw: pinMsg(2) }, "g@g.us", ctx)).toBeNull();
  });

  test("only a pin is worth storing", () => {
    expect(isStorable(pinMsg(1))).toBe(true);
    expect(isStorable(pinMsg(1, { wrap: true }))).toBe(true);
    expect(isStorable(pinMsg(2))).toBe(false);
    expect(isStorable(pinMsg(0))).toBe(false);
  });
});

// ---------------------------------------------------------------------------------------------------------------
// Content types that used to fall through to "Unsupported message", albums, mentions and link previews.

const G = "g@g.us";
const LINH = "84901@s.whatsapp.net";
const HOA_PN = "84902@s.whatsapp.net";
const HOA_LID = "68388898529359@lid";
const STRANGER = "84977@s.whatsapp.net";
const ME_LID = "55555555555555@lid";

const names: Record<string, string> = { [LINH]: "Linh", [HOA_PN]: "Hoa Tran", "84900@s.whatsapp.net": "Me (contact)" };
const mctx: MapCtx = {
  selfId: "84900@s.whatsapp.net",
  selfName: "Me",
  // Baileys' canonical form: a LID with a known phone number is that phone number.
  canon: (j) => (j === HOA_LID ? HOA_PN : j === ME_LID ? "84900@s.whatsapp.net" : (j ?? "")),
  nameOf: (j) => names[j],
  groupSize: () => undefined,
  aliases: (j) => (j === HOA_PN || j === HOA_LID ? [HOA_PN, HOA_LID] : [j]),
};

const rawOf = (message: any, extra: any = {}) => ({
  key: { remoteJid: G, fromMe: false, id: "M1", participant: LINH },
  messageTimestamp: TS,
  message,
  ...extra,
});
const map = (message: any, extra: any = {}, c: MapCtx = mctx) => mapMessage({ raw: rawOf(message, extra) }, G, c);
const storable = (message: any) => isStorable(rawOf(message));

const image = (extra: any = {}) => ({ imageMessage: { mimetype: "image/jpeg", width: 100, height: 80, ...extra } });
const assoc = (type: number, parent = "ALBUM1") => ({ messageAssociation: { associationType: type, parentMessageKey: { remoteJid: G, fromMe: false, id: parent, participant: LINH } } });
const A = proto.MessageAssociation.AssociationType;

describe("protocol-only content never becomes a bubble", () => {
  const protocolOnly: Record<string, any> = {
    "an edit": { protocolMessage: { type: 14, key: target, editedMessage: { conversation: "new" } } },
    "an edit in its future-proof wrapper": { editedMessage: { message: { protocolMessage: { type: 14, key: target, editedMessage: { conversation: "new" } } } } },
    "a revoke": { protocolMessage: { type: 0, key: target } },
    "a reaction": { reactionMessage: { key: target, text: "👍" } },
    "an encrypted reaction": { encReactionMessage: { targetMessageKey: target, encPayload: new Uint8Array(4) } },
    "a poll vote": { pollUpdateMessage: { pollCreationMessageKey: target } },
    "a keep-in-chat": { keepInChatMessage: { key: target, keepType: 1 } },
    "an encrypted event response": { encEventResponseMessage: { eventCreationMessageKey: target } },
    "a secret-encrypted edit": { messageContextInfo: { messageSecret: new Uint8Array(32) }, secretEncryptedMessage: { targetMessageKey: target, secretEncType: 2 } },
    "a sender key distribution": { senderKeyDistributionMessage: { groupId: G, axolotlSenderKeyDistributionMessage: new Uint8Array(4) } },
    "a fast-ratchet sender key distribution": { fastRatchetKeySenderKeyDistributionMessage: { groupId: G } },
    "an album parent": { messageContextInfo: { messageSecret: new Uint8Array(32) }, albumMessage: { expectedImageCount: 0, expectedVideoCount: 0 } },
    "a reaction inside the device-sent wrapper": { deviceSentMessage: { destinationJid: G, message: { reactionMessage: { key: target, text: "👍" } } } },
    "a scheduled call edit": { scheduledCallEditMessage: { editType: 1 } },
  };
  for (const [what, message] of Object.entries(protocolOnly)) {
    test(`${what}`, () => {
      expect(map(message)).toBeNull();
      expect(storable(message)).toBe(false);
    });
  }

  test("responses with nothing to read are protocol traffic as well", () => {
    for (const message of [{ buttonsResponseMessage: { selectedButtonId: "b1" } }, { interactiveResponseMessage: { nativeFlowResponseMessage: { name: "x" } } }]) {
      expect(map(message)).toBeNull();
      expect(storable(message)).toBe(false);
    }
  });
});

describe("types that were 'Unsupported message'", () => {
  test("a placeholder says the content is on the phone", () => {
    const m = map({ placeholderMessage: { type: 0 } })!;
    expect(m.kind).toBe("unsupported");
    expect(m.text).toBe("This message is not available here. Open WhatsApp on your phone to read it.");
    expect(storable({ placeholderMessage: { type: 0 } })).toBe(true); // the real content can replace it later
  });

  test("a Lottie sticker shows what the animation depicts", () => {
    const inner = { stickerMessage: { mimetype: "application/was", isLottie: true, accessibilityLabel: "A bird giving a thumbs up" } };
    expect(map({ lottieStickerMessage: { message: inner } })).toMatchObject({ kind: "unsupported", text: "Animated sticker\nA bird giving a thumbs up" });
    expect(map({ stickerMessage: inner.stickerMessage })).toMatchObject({ kind: "unsupported", text: "Animated sticker\nA bird giving a thumbs up" });
    expect(map({ lottieStickerMessage: { message: { stickerMessage: { isLottie: true } } } })!.text).toBe("Animated sticker");
  });

  test("an ordinary sticker stays a sticker", () => {
    expect(map({ stickerMessage: { mimetype: "image/webp" } })).toMatchObject({ kind: "sticker", media: { mime: "image/webp" } });
  });

  test("view-once (v1, v2, v2 extension) and disappearing wrappers show the content inside", () => {
    for (const w of ["viewOnceMessage", "viewOnceMessageV2", "viewOnceMessageV2Extension", "ephemeralMessage"]) {
      expect(map({ [w]: { message: image({ caption: "look" }) } })).toMatchObject({ kind: "image", text: "look" });
    }
  });

  test("a video note is a video", () => {
    expect(map({ ptvMessage: { mimetype: "video/mp4", seconds: 7 } })).toMatchObject({ kind: "video", media: { duration: 7 } });
  });

  test("polls of every generation and their result snapshots read as text", () => {
    const poll = { name: "Lunch?", options: [{ optionName: "Pho" }, { optionName: "Bun" }] };
    for (const message of [{ pollCreationMessage: poll }, { pollCreationMessageV3: poll }, { pollCreationMessageV5: poll }, { pollCreationMessageV4: { message: { pollCreationMessage: poll } } }]) {
      expect(map(message)).toMatchObject({ kind: "unsupported", text: "Poll: Lunch?\n- Pho\n- Bun" });
    }
    const snap = { name: "Lunch?", pollVotes: [{ optionName: "Pho", optionVoteCount: 3 }, { optionName: "Bun", optionVoteCount: { low: 1, high: 0 } }] };
    expect(map({ pollResultSnapshotMessage: snap })!.text).toBe("Poll results: Lunch?\n- Pho: 3\n- Bun: 1");
    expect(map({ pollResultSnapshotMessageV3: snap })!.text).toContain("Poll results: Lunch?");
  });

  test("events, also cancelled ones", () => {
    expect(map({ eventMessage: { name: "Party" } })).toMatchObject({ kind: "unsupported", text: "Event: Party" });
    expect(map({ eventMessage: { name: "Party", isCanceled: true } })!.text).toBe("Cancelled event: Party");
  });

  test("group and channel invitations, sticker packs", () => {
    expect(map({ groupInviteMessage: { groupName: "Team", inviteCode: "ABC" } })).toMatchObject({ kind: "link", text: "Group invitation: Team\nhttps://chat.whatsapp.com/ABC" });
    expect(map({ newsletterAdminInviteMessage: { newsletterName: "News", caption: "join us" } })).toMatchObject({ kind: "text", text: "Channel admin invitation: News\njoin us" });
    expect(map({ newsletterFollowerInviteMessageV2: { newsletterName: "News" } })!.text).toBe("Channel invitation: News");
    expect(map({ stickerPackMessage: { name: "Cats", publisher: "Ann" } })!.text).toBe("Sticker pack: Cats\nAnn");
  });

  test("buttons, lists, templates, interactive cards and their answers", () => {
    expect(map({ buttonsResponseMessage: { selectedDisplayText: "Yes" } })).toMatchObject({ kind: "text", text: "Yes" });
    expect(map({ templateButtonReplyMessage: { selectedDisplayText: "No" } })!.text).toBe("No");
    expect(map({ listResponseMessage: { title: "Option 2" } })!.text).toBe("Option 2");
    expect(map({ interactiveResponseMessage: { body: { text: "Confirmed" } } })).toMatchObject({ kind: "text", text: "Confirmed" });
    expect(map({ interactiveMessage: { header: { title: "Sale" }, body: { text: "50% off" } } })!.text).toBe("Sale\n50% off");
    expect(map({ interactiveMessage: { nativeFlowMessage: { buttons: [] } } })!.text).toBe("Interactive message");
    expect(map({ templateMessage: { interactiveMessageTemplate: { body: { text: "Your code is 123" } } } })!.text).toBe("Your code is 123");
    expect(map({ highlyStructuredMessage: { fallbackLg: "Hello from a business" } })!.text).toBe("Hello from a business");
  });

  test("shop and payment messages read as one line", () => {
    expect(map({ orderMessage: { orderTitle: "Shoes", itemCount: 2, totalAmount1000: 150000, totalCurrencyCode: "VND", message: "pls ship" } })!.text).toBe("Order: Shoes\n2 items\n150 VND\npls ship");
    expect(map({ productMessage: { product: { title: "Hat", priceAmount1000: 5000, currencyCode: "USD" }, body: "nice" } })!.text).toBe("Product: Hat\n5 USD\nnice");
    expect(map({ requestPaymentMessage: { amount1000: 20000, currencyCodeIso4217: "INR", noteMessage: { conversation: "rent" } } })!.text).toBe("Payment request\n20 INR\nrent");
    expect(map({ sendPaymentMessage: {} })!.text).toBe("Payment sent");
    expect(map({ declinePaymentRequestMessage: {} })!.text).toBe("Payment request declined");
    expect(map({ cancelPaymentRequestMessage: {} })!.text).toBe("Payment request cancelled");
    expect(map({ paymentInviteMessage: {} })!.text).toBe("Payment invitation");
    expect(map({ scheduledCallCreationMessage: { title: "Stand-up" } })!.text).toBe("Scheduled call: Stand-up");
  });

  test("an AI answer shows its text sections", () => {
    expect(map({ richResponseMessage: { submessages: [{ messageText: "Hello" }, { codeMetadata: {} }, { messageText: "World" }] } })).toMatchObject({ kind: "text", text: "Hello\nWorld" });
  });

  test("wrappers Baileys leaves closed are opened", () => {
    for (const w of ["groupMentionedMessage", "botInvokeMessage", "botForwardedMessage", "commentMessage"]) {
      expect(map({ [w]: { message: { conversation: "inside" } } })).toMatchObject({ kind: "text", text: "inside" });
    }
    expect(map({ commentMessage: {} })).toBeNull();
  });

  test("what remains unsupported says which type it was", () => {
    expect(map({ somethingNewMessage: { contextInfo: {} } })).toMatchObject({ kind: "unsupported", text: "Unsupported message (somethingNewMessage)" });
  });

  test("a quoted placeholder or lottie sticker gets a readable quote text", () => {
    const quoted = { placeholderMessage: { type: 0 } };
    const m = map({ extendedTextMessage: { text: "what was that?", contextInfo: { stanzaId: "Q1", participant: LINH, quotedMessage: quoted } } })!;
    expect(m.replyTo).toMatchObject({ id: "Q1", kind: "unsupported" });
    expect(m.replyTo!.text).toContain("WhatsApp on your phone");
  });
});

describe("albums", () => {
  test("a photo that points at its album parent carries the parent's id as groupId", () => {
    const m = map({ messageContextInfo: assoc(A.MEDIA_ALBUM), ...image({ caption: "one" }) }, { key: { remoteJid: G, fromMe: false, id: "C1", participant: LINH } })!;
    expect(m).toMatchObject({ id: "C1", kind: "image", text: "one", groupId: "ALBUM1" });
  });

  test("videos too, and the association may sit inside the associated-child wrapper", () => {
    expect(map({ messageContextInfo: assoc(A.MEDIA_ALBUM), videoMessage: { mimetype: "video/mp4", seconds: 3 } })).toMatchObject({ kind: "video", groupId: "ALBUM1" });
    const wrapped = { associatedChildMessage: { message: { ...image(), messageContextInfo: assoc(A.MEDIA_ALBUM, "ALBUM2") } } };
    expect(map(wrapped)).toMatchObject({ kind: "image", groupId: "ALBUM2" });
    expect(map({ messageContextInfo: assoc(A.MEDIA_ALBUM, "ALBUM3"), associatedChildMessage: { message: image() } })).toMatchObject({ groupId: "ALBUM3" });
  });

  test("the association is read from the stored (plain, enum-as-number) form and from names", () => {
    expect(association({ message: { messageContextInfo: assoc(1) } })).toEqual({ parentId: "ALBUM1", type: 1 });
    expect(association({ message: { messageContextInfo: { messageAssociation: { associationType: "MEDIA_ALBUM", parentMessageKey: { id: "P" } } } } })).toEqual({ parentId: "P", type: 1 });
    expect(association({ message: image() })).toBeUndefined();
  });

  test("ordinary photos have no groupId; other associations do not make an album", () => {
    expect(map(image())!.groupId).toBeUndefined();
    expect(map({ messageContextInfo: assoc(A.BOT_PLUGIN), ...image() })).toMatchObject({ kind: "image" });
    expect(map({ messageContextInfo: assoc(A.BOT_PLUGIN), ...image() })!.groupId).toBeUndefined();
  });

  test("the HD / HEVC / motion-photo companions of a photo are not bubbles of their own", () => {
    const hd = { messageContextInfo: assoc(A.HD_IMAGE_DUAL_UPLOAD, "SD1"), ...image({ caption: "same caption" }) };
    expect(map(hd)).toBeNull();
    expect(storable(hd)).toBe(false);
    expect(map({ messageContextInfo: assoc(A.HD_VIDEO_DUAL_UPLOAD, "V1"), videoMessage: { mimetype: "video/mp4" } })).toBeNull();
    expect(map({ messageContextInfo: assoc(A.HEVC_VIDEO_DUAL_UPLOAD, "V1"), videoMessage: { mimetype: "video/mp4" } })).toBeNull();
    expect(map({ messageContextInfo: assoc(A.MOTION_PHOTO, "I1"), videoMessage: { mimetype: "video/mp4" } })).toBeNull();
    expect(storable({ associatedChildMessage: { message: { ...image(), messageContextInfo: assoc(A.HD_IMAGE_DUAL_UPLOAD, "SD1") } } })).toBe(false);
  });

  test("album photos are stored, only the parent and the companions are not", () => {
    expect(storable({ messageContextInfo: assoc(A.MEDIA_ALBUM), ...image() })).toBe(true);
    expect(storable({ albumMessage: { expectedImageCount: 3 } })).toBe(false);
  });

  test("a recalled photo is a plain tombstone", () => {
    expect(mapMessage({ raw: rawOf({ messageContextInfo: assoc(A.MEDIA_ALBUM), ...image() }), deleted: true }, G, mctx)).toMatchObject({ deleted: true });
  });
});

describe("mentions", () => {
  const tag = (text: string, mentionedJid: string[], extra: any = {}) => map({ extendedTextMessage: { text, contextInfo: { mentionedJid } } }, extra, mctx)!;

  test("a LID token becomes the contact's name, a phone token too", () => {
    expect(tag("hi @68388898529359 and @84901!", [HOA_LID, LINH]).text).toBe("hi @Hoa Tran and @Linh!");
  });

  test("the text may spell the person differently from mentionedJid (LID vs phone number)", () => {
    expect(tag("hey @84902", [HOA_LID]).text).toBe("hey @Hoa Tran");
    expect(tag("hey @68388898529359", [HOA_PN]).text).toBe("hey @Hoa Tran");
  });

  test("yourself is shown with the account's own name, also through a LID", () => {
    expect(tag("@84900 and @55555555555555", ["84900@s.whatsapp.net", ME_LID]).text).toBe("@Me and @Me");
  });

  test("an unnamed phone number falls back to +number; an unresolvable LID stays for a later sync", () => {
    expect(tag("cc @84977", [STRANGER]).text).toBe("cc @+84977");
    expect(tag("cc @999999999999", ["999999999999@lid"]).text).toBe("cc @999999999999");
  });

  test("only the tagged people are rewritten; digits glued to a tag and repeated tags are fine", () => {
    expect(tag("@84901 mail a@84902.com and @84901x", [LINH]).text).toBe("@Linh mail a@84902.com and @Linhx");
    expect(tag("@684902 @84901 @84901", [LINH]).text).toBe("@684902 @Linh @Linh");
    expect(tag("@8490112345 ok", [LINH]).text).toBe("@Linh12345 ok");
  });

  test("a name with regex replacement characters is inserted literally", () => {
    const c: MapCtx = { ...mctx, nameOf: () => "A $& $1 B" };
    expect(resolveMentions("hi @84901", [LINH], c)).toBe("hi @A $& $1 B");
  });

  test("captions of photos and files, and the text of a quoted message", () => {
    expect(map({ ...image({ caption: "for @68388898529359", contextInfo: { mentionedJid: [HOA_LID] } }) })!.text).toBe("for @Hoa Tran");
    const quoted = { extendedTextMessage: { text: "ask @84901", contextInfo: { mentionedJid: [LINH] } } };
    const m = map({ extendedTextMessage: { text: "ok", contextInfo: { stanzaId: "Q1", participant: HOA_LID, quotedMessage: quoted } } })!;
    expect(m.replyTo).toMatchObject({ id: "Q1", text: "ask @Linh", senderName: "Hoa Tran" });
  });

  test("a message that was sent from here: Baileys' form (text with @number, mentionedJid) reads with the name", () => {
    const sent = {
      key: { remoteJid: G, fromMe: true, id: "OUT1" },
      messageTimestamp: TS,
      message: { extendedTextMessage: { text: "lunch @68388898529359?", contextInfo: { mentionedJid: [HOA_LID] } } },
    };
    expect(mapMessage({ raw: sent }, G, mctx)).toMatchObject({ fromMe: true, text: "lunch @Hoa Tran?" });
  });

  test("without a mention list nothing is touched; with no aliases hook it still works", () => {
    expect(map({ conversation: "write to @84901" })!.text).toBe("write to @84901");
    const { aliases: _a, ...bare } = mctx;
    expect(resolveMentions("hey @84902", [HOA_LID], bare as MapCtx)).toBe("hey @Hoa Tran"); // canon() already gives the phone jid
    expect(resolveMentions("hey @68388898529359", [HOA_PN], bare as MapCtx)).toBe("hey @68388898529359"); // the LID spelling needs aliases()
    expect(resolveMentions("hey @68388898529359", [HOA_PN], mctx)).toBe("hey @Hoa Tran");
  });
});

describe("link previews", () => {
  const ext = (o: any) => map({ extendedTextMessage: o })!;

  test("a URL that is written in the text needs nothing", () => {
    const m = ext({ text: "see https://example.com/a now", matchedText: "https://example.com/a", canonicalUrl: "https://example.com/a" });
    expect(m).toMatchObject({ kind: "link", text: "see https://example.com/a now" });
    expect(m.links).toBeUndefined();
  });

  test("a bare domain or a trailing slash still counts as written", () => {
    expect(ext({ text: "example.com/a", matchedText: "example.com/a", canonicalUrl: "https://example.com/a" }).links).toBeUndefined();
    expect(ext({ text: "go to HTTPS://Example.com", matchedText: "https://example.com/", canonicalUrl: "https://example.com/" }).links).toBeUndefined();
  });

  test("a preview whose URL the text does not contain links the text to the canonical URL", () => {
    const m = ext({ text: "great article 🎉", matchedText: "bit.ly/xyz", canonicalUrl: "https://example.com/long/article" });
    expect(m.kind).toBe("link");
    expect(m.links).toEqual([{ offset: 0, length: "great article 🎉".length, url: "https://example.com/long/article" }]);
  });

  test("with only matchedText the URL gets a scheme", () => {
    expect(ext({ text: "look", matchedText: "example.org/x" }).links).toEqual([{ offset: 0, length: 4, url: "https://example.org/x" }]);
  });

  test("a preview with no text at all shows its URL as the text", () => {
    expect(ext({ text: "", canonicalUrl: "https://example.com/a", title: "A" })).toMatchObject({ kind: "link", text: "https://example.com/a" });
  });

  test("offsets refer to the final text, after mentions were rewritten", () => {
    const m = ext({ text: "@84901 read", canonicalUrl: "https://example.com/a", contextInfo: { mentionedJid: [LINH] } });
    expect(m.text).toBe("@Linh read");
    expect(m.links).toEqual([{ offset: 0, length: "@Linh read".length, url: "https://example.com/a" }]);
  });

  test("plain text and messages without a preview have no links", () => {
    expect(ext({ text: "just words" })).toMatchObject({ kind: "text", text: "just words" });
    expect(ext({ text: "just words" }).links).toBeUndefined();
  });
});
