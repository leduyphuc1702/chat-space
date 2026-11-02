// Phone-sync message -> the `data` object of a zca-js Message, the same way Zalo PC turns a synced row into a
// "server message" (shared-worker: class `El.syncMessage2SVMessage`, enum `Ai`, table `ji`).

/** One decoded Sync2.Message.Message (see proto.ts; repeated fields are `<name>List`). */
export interface SyncMessage {
  senderId: string;
  clientId: number;
  msgStatus: number;
  msgType: number;
  timestamp: number;
  /** decimal string; "0" for messages the phone never got a server id for */
  globalId: string;
  content: string;
  meta?: SyncMeta;
}

export interface SyncAttach {
  type: string;
  extInfo: string;
  action: string;
  params: string;
  title: string;
  href: string;
  thumb: string;
  description: string;
  catId: number;
  id: number;
  childNumber: number;
  remains: string;
  zinstantData: string;
  zinstantMsg: string;
}

export interface SyncMeta {
  attachsList?: SyncAttach[];
  mentionsList?: { uid: string; pos: number; len: number; type: number }[];
  reference?: { type: number; data: string };
  quote?: {
    attach: string;
    ownerId: string;
    ts: number;
    fromD: string;
    cliMsgId: number;
    globalMsgId: string;
    msg: string;
    cliMsgType: number;
    ttl: number;
    partialQuote: string;
  };
  property?: { size: number; color: number; type: number; subType: number; ext: string };
  ttl?: number;
}

/** Sync2 `msgType` values (Zalo PC enum `Ai`). */
export const SyncType = {
  Text: 0,
  Doodle: 2,
  Photo: 3,
  Photo_V2: 4,
  Voice: 6,
  Sticker: 10,
  Contact: 12,
  OA: 15,
  Location: 18,
  Video: 19,
  ChatInfo: 20,
  ECard: 21,
  File: 22,
  Gift: 23,
  Webcontent: 24,
  VideoLive: 25,
  Poll: 26,
  PhotoV2: 31,
  ChatDelete: 33,
  Undo: 36,
  WebcontentV2: 53,
} as const;

/** Sync2 type -> web msgType (Zalo PC table `ji`). Types missing here are dropped by Zalo PC too. */
const WEB_TYPE: Record<number, string> = {
  [SyncType.Text]: "webchat",
  [SyncType.Doodle]: "chat.doodle",
  [SyncType.Photo]: "chat.photo",
  [SyncType.Photo_V2]: "chat.photo",
  [SyncType.PhotoV2]: "chat.photo",
  [SyncType.Voice]: "chat.voice",
  [SyncType.Sticker]: "chat.sticker",
  [SyncType.Contact]: "chat.recommended",
  [SyncType.OA]: "chat.list.action",
  [SyncType.Location]: "chat.location.new",
  [SyncType.Video]: "chat.video.msg",
  [SyncType.File]: "share.file",
  [SyncType.Gift]: "chat.gif",
  [SyncType.Webcontent]: "chat.webcontent",
  [SyncType.WebcontentV2]: "chat.webcontent.v2",
};

/** Kinds whose caption may come in `content` rather than in the attachment title. */
const CAPTIONED = new Set<number>([SyncType.Photo, SyncType.Photo_V2, SyncType.PhotoV2, SyncType.Doodle, SyncType.Video, SyncType.Gift]);

/** msgType we give recalled messages; the caller shows them as "message recalled". */
export const UNDO_TYPE = "chat.undo";

export interface ConvertCtx {
  isGroup: boolean;
  /** zca-js threadId: the peer uid (1:1) or group id */
  threadId: string;
  /** uid of the sender, "" if unknown */
  senderUid: string;
  fromMe: boolean;
  selfId: string;
}

/** Web-format attachment object, as zca-js delivers it in `content` (lower-case `childnumber`). */
function webAttach(a: SyncAttach | undefined): Record<string, unknown> {
  if (!a) return {};
  const out: Record<string, unknown> = {
    title: a.title ?? "",
    description: a.description ?? "",
    href: a.href ?? "",
    thumb: a.thumb ?? "",
    params: a.params ?? "",
    action: a.action ?? "",
    type: a.type ?? "",
    childnumber: a.childNumber ?? 0,
  };
  if (a.id) out.id = a.id;
  if (a.catId) out.catId = a.catId;
  if (a.extInfo) out.extInfo = a.extInfo;
  return out;
}

function fileExt(params: string | undefined): string | undefined {
  if (!params) return undefined;
  try {
    const ext = JSON.parse(params)?.fileExt;
    return typeof ext === "string" && /^[\w-]{1,10}$/.test(ext) ? ext : undefined;
  } catch {
    return undefined;
  }
}

/**
 * Returns the zca-js-like `data` of a synced message, or undefined for kinds Zalo PC drops as well
 * (polls, e-cards, group notices) and for malformed rows.
 */
export function syncToZaloMessage(sm: SyncMessage, ctx: ConvertCtx): Record<string, unknown> | undefined {
  const meta = sm.meta ?? {};
  const attach = meta.attachsList?.[0];
  const undo = sm.msgType === SyncType.Undo;
  const msgType = undo ? UNDO_TYPE : WEB_TYPE[sm.msgType];
  if (!msgType) return undefined;
  if (sm.msgType === SyncType.Webcontent && !attach?.params?.includes("pcItem")) return undefined;
  const cliMsgId = String(sm.clientId ?? "");
  const globalId = String(sm.globalId ?? "");
  if ((!cliMsgId || cliMsgId === "0") && (!globalId || globalId === "0")) return undefined;

  let content: unknown;
  switch (sm.msgType) {
    case SyncType.Text:
      // Rich text keeps the text in the attachment title (style ranges in params).
      content = attach?.action === "rtf" ? { ...webAttach(attach), title: attach.title || sm.content, msg: attach.title || sm.content } : sm.content;
      break;
    case SyncType.Undo:
      content = "";
      break;
    case SyncType.OA:
      content = (meta.attachsList ?? []).map(webAttach);
      break;
    case SyncType.File: {
      const f = webAttach(attach);
      const ext = fileExt(attach?.params);
      if (ext && typeof f.title === "string" && f.title && !f.title.toLowerCase().endsWith(`.${ext.toLowerCase()}`)) f.title += `.${ext}`;
      content = f;
      break;
    }
    case SyncType.WebcontentV2:
      content = { zinstant_msg: attach?.zinstantMsg ?? "", zinstant_data: attach?.zinstantData ?? "", action: attach?.action ?? "" };
      break;
    default:
      content = webAttach(attach);
  }
  if (CAPTIONED.has(sm.msgType) && sm.content && !(content as any).title && !sm.content.startsWith("{")) {
    (content as any).title = sm.content;
  }

  const quote = meta.quote && meta.quote.globalMsgId && meta.quote.globalMsgId !== "0" ? { ...meta.quote } : undefined;
  return {
    msgId: globalId && globalId !== "0" ? globalId : "",
    cliMsgId,
    msgType,
    uidFrom: ctx.fromMe ? ctx.selfId : ctx.senderUid,
    idTo: ctx.fromMe || ctx.isGroup ? ctx.threadId : ctx.selfId,
    ts: String(sm.timestamp ?? 0),
    content,
    quote,
    ttl: Number(meta.ttl) || 0,
    propertyExt: meta.property,
    dName: "",
    recalled: undo || undefined,
  };
}
