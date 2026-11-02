import { describe, expect, test } from "bun:test";
import { editedContent } from "./whatsapp-store.ts";

const quote = { stanzaId: "Q1", participant: "123@s.whatsapp.net", quotedMessage: { conversation: "original" } };

describe("editedContent", () => {
  test("a plain text message takes the new text", () => {
    expect(editedContent({ conversation: "helo" }, { conversation: "hello" })).toEqual({ conversation: "hello" });
  });

  test("a reply keeps its quote when the edit is a bare conversation", () => {
    const old = { extendedTextMessage: { text: "ok", contextInfo: quote } };
    expect(editedContent(old, { conversation: "okay" })).toEqual({ extendedTextMessage: { text: "okay", contextInfo: quote } });
  });

  test("an edit that adds mentions merges them into the quote context", () => {
    const old = { extendedTextMessage: { text: "hi", contextInfo: quote } };
    const out = editedContent(old, { extendedTextMessage: { text: "hi @84901", contextInfo: { mentionedJid: ["84901@s.whatsapp.net"] } } });
    expect(out.extendedTextMessage.contextInfo).toEqual({ ...quote, mentionedJid: ["84901@s.whatsapp.net"] });
    expect(out.extendedTextMessage.text).toBe("hi @84901");
  });

  test("a link preview is dropped when the edit is plain text", () => {
    const old = { extendedTextMessage: { text: "see https://a.vn", matchedText: "https://a.vn", title: "A", canonicalUrl: "https://a.vn" } };
    expect(editedContent(old, { conversation: "no link now" })).toEqual({ extendedTextMessage: { text: "no link now" } });
  });

  test("a photo keeps its media keys and gets the text as caption", () => {
    const old = { imageMessage: { url: "u", mediaKey: "k", caption: "old", contextInfo: quote } };
    expect(editedContent(old, { conversation: "new caption" })).toEqual({
      imageMessage: { url: "u", mediaKey: "k", caption: "new caption", contextInfo: quote },
    });
    expect(editedContent(old, { imageMessage: { caption: "via image edit" } }).imageMessage).toMatchObject({ url: "u", mediaKey: "k", caption: "via image edit" });
  });

  test("wrapped content (disappearing chats) is edited inside the wrapper", () => {
    const old = { ephemeralMessage: { message: { extendedTextMessage: { text: "a", contextInfo: { expiration: 86400 } } } } };
    const out = editedContent(old, { conversation: "b" });
    expect(out.ephemeralMessage.message.extendedTextMessage).toEqual({ text: "b", contextInfo: { expiration: 86400 } });
  });

  test("the original object is not mutated", () => {
    const old = { conversation: "x" };
    editedContent(old, { conversation: "y" });
    expect(old).toEqual({ conversation: "x" });
  });
});
