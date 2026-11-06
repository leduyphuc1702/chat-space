package main

// What an end-to-end encrypted payload becomes in ChatSpace.
//
// An FBMessage carries one of: a ConsumerApplication (the plain WhatsApp-style content:
// text, media, reactions, edits, revokes ...), an Armadillo (Messenger's richer content:
// link cards, location sharing, call logs, story replies, photo albums, "likes" ...),
// or a type whatsmeow only exposes as an opaque sub-protocol (voip signalling, business,
// payments, Instagram). classify maps all of these to either protocol-only actions that
// must not create a bubble (reaction, edit, revoke, receipts-like signals), or to the
// parts of one or more Messages. Only what has no sensible text is shown as
// kind "unsupported" with a short, specific sentence.

import (
	"context"
	"fmt"
	"net/url"
	"regexp"
	"strconv"
	"strings"
	"time"
	"unicode/utf16"

	armadillo "go.mau.fi/whatsmeow/proto"
	"go.mau.fi/whatsmeow/proto/armadilloutil"
	"go.mau.fi/whatsmeow/proto/waArmadilloApplication"
	"go.mau.fi/whatsmeow/proto/waArmadilloXMA"
	"go.mau.fi/whatsmeow/proto/waCommon"
	"go.mau.fi/whatsmeow/proto/waConsumerApplication"
	"go.mau.fi/whatsmeow/proto/waMediaTransport"
	"go.mau.fi/whatsmeow/proto/waMsgApplication"
	"go.mau.fi/whatsmeow/types/events"
)

const (
	unsupportedE2EEText       = "Tin nhắn chưa được hỗ trợ (mở ứng dụng Messenger để xem)"
	legacyUnsupportedE2EEText = "Unsupported encrypted message (open it in the Messenger app)"
	unknownMentionName        = "Người dùng Facebook"
)

// unsupportedText is the sentence for a payload ChatSpace cannot show.
func unsupportedText(what string) string {
	if what == "" {
		return unsupportedE2EEText
	}
	return what + " (mở ứng dụng Messenger để xem)"
}

func unsupportedPart(what string) waContent {
	return waContent{kind: "unsupported", text: unsupportedText(what)}
}

// waContent is one Message worth of decoded content.
type waContent struct {
	kind  string
	text  string
	media *Media
	ref   *waMediaRef
	links []Link
}

// e2eeAction is the result of classify: exactly one of the first four is set, or parts.
type e2eeAction struct {
	reaction *waConsumerApplication.ConsumerApplication_ReactionMessage
	edit     *waConsumerApplication.ConsumerApplication_EditMessage
	revokeID string
	parts    []waContent
	// unsupported names the Go type of a payload shown as unsupported (for the log).
	unsupported string
}

// classify decodes an FBMessage. who is how the sender is called in sentences about them
// ("You" for our own messages). Protocol-only payloads give an action with no parts.
func (e *e2eeState) classify(evt *events.FBMessage, chat, who string) (act e2eeAction) {
	switch app := evt.Message.(type) {
	case *waConsumerApplication.ConsumerApplication:
		switch p := app.GetPayload().GetPayload().(type) {
		case *waConsumerApplication.ConsumerApplication_Payload_Content:
			switch v := p.Content.GetContent().(type) {
			case *waConsumerApplication.ConsumerApplication_Content_ReactionMessage:
				act.reaction = v.ReactionMessage
				return act
			case *waConsumerApplication.ConsumerApplication_Content_EditMessage:
				act.edit = v.EditMessage
				return act
			case *waConsumerApplication.ConsumerApplication_Content_PollUpdateMessage:
				return act // a vote: the poll itself is the bubble
			}
			act.parts = e.consumerParts(chat, p.Content)
		case *waConsumerApplication.ConsumerApplication_Payload_ApplicationData:
			if rev := p.ApplicationData.GetRevoke(); rev != nil {
				act.revokeID = rev.GetKey().GetID()
			}
			// Signal and sub-protocol payloads carry no content either.
		}
	case *waArmadilloApplication.Armadillo:
		act.parts = e.armadilloParts(chat, who, app)
	case *armadillo.Unsupported_Voip:
		// call signalling; the call log arrives as an Armadillo ExtendedContentMessage
	case *armadillo.Unsupported_BusinessApplication:
		act.parts = []waContent{unsupportedPart("Tin nhắn doanh nghiệp")}
	case *armadillo.Unsupported_PaymentApplication:
		act.parts = []waContent{unsupportedPart("Payment")}
	default:
		// Instagram: the add payload is a message, supplements/deletes are not.
		name := fmt.Sprintf("%T", evt.Message)
		if strings.HasSuffix(name, "AddMessagePayload") {
			act.parts = []waContent{unsupportedPart("Tin nhắn Instagram")}
		}
	}
	for _, p := range act.parts {
		if p.kind == "unsupported" {
			act.unsupported = fmt.Sprintf("%T", evt.Message)
			break
		}
	}
	return act
}

// Consumer content -----------------------------------------------------------------------

func (e *e2eeState) mentionName(chat, user string) string { return e.b.mentionName(chat, user) }

// textOf is the readable text of a MessageText: mention tokens become "@Name".
func (e *e2eeState) textOf(chat string, mt *waCommon.MessageText) string {
	return resolveE2EEMentions(mt.GetText(), mt.GetMentionedJID(), mt.GetMentions(), func(u string) string { return e.mentionName(chat, u) })
}

func (e *e2eeState) consumerParts(chat string, content *waConsumerApplication.ConsumerApplication_Content) []waContent {
	c := waContent{kind: "text"}
	switch v := content.GetContent().(type) {
	case *waConsumerApplication.ConsumerApplication_Content_MessageText:
		c.text = e.textOf(chat, v.MessageText)
	case *waConsumerApplication.ConsumerApplication_Content_ExtendedTextMessage:
		return []waContent{e.extendedText(chat, v.ExtendedTextMessage)}
	case *waConsumerApplication.ConsumerApplication_Content_StatusTextMessage:
		return []waContent{e.extendedText(chat, v.StatusTextMessage.GetText())}
	case *waConsumerApplication.ConsumerApplication_Content_ImageMessage:
		return []waContent{e.decodeImage(chat, v.ImageMessage)}
	case *waConsumerApplication.ConsumerApplication_Content_VideoMessage:
		return []waContent{e.decodeVideo(chat, v.VideoMessage)}
	case *waConsumerApplication.ConsumerApplication_Content_StickerMessage:
		return []waContent{e.decodeSticker(v.StickerMessage)}
	case *waConsumerApplication.ConsumerApplication_Content_AudioMessage:
		dec, err := v.AudioMessage.Decode()
		if err != nil {
			c.text = "Tin nhắn thoại (không giải mã được)"
			return []waContent{c}
		}
		c.kind = "voice"
		c.media, c.ref = transportInfo(dec.GetIntegral().GetTransport(), "audio")
		c.media.Duration = float64(dec.GetAncillary().GetSeconds())
	case *waConsumerApplication.ConsumerApplication_Content_DocumentMessage:
		dec, err := v.DocumentMessage.Decode()
		if err != nil {
			c.text = "Tệp (không giải mã được)"
			return []waContent{c}
		}
		c.kind = "file"
		c.media, c.ref = transportInfo(dec.GetIntegral().GetTransport(), "document")
		c.media.FileName = v.DocumentMessage.GetFileName()
	case *waConsumerApplication.ConsumerApplication_Content_ViewOnceMessage:
		switch vv := v.ViewOnceMessage.GetViewOnceContent().(type) {
		case *waConsumerApplication.ConsumerApplication_ViewOnceMessage_ImageMessage:
			return []waContent{e.decodeImage(chat, vv.ImageMessage)}
		case *waConsumerApplication.ConsumerApplication_ViewOnceMessage_VideoMessage:
			return []waContent{e.decodeVideo(chat, vv.VideoMessage)}
		}
		return []waContent{unsupportedPart("Tin nhắn xem một lần")}
	case *waConsumerApplication.ConsumerApplication_Content_LocationMessage:
		loc := v.LocationMessage.GetLocation()
		c.kind = "location"
		c.text = strings.TrimSpace(loc.GetName() + "\n" + v.LocationMessage.GetAddress())
		if c.text == "" {
			c.text = "Location"
		}
		c.links = mapLinks(c.text, loc.GetDegreesLatitude(), loc.GetDegreesLongitude())
	case *waConsumerApplication.ConsumerApplication_Content_LiveLocationMessage:
		loc := v.LiveLocationMessage.GetLocation()
		c.kind = "location"
		c.text = firstNonEmpty(e.textOf(chat, v.LiveLocationMessage.GetCaption()), "Vị trí trực tiếp")
		c.links = mapLinks(c.text, loc.GetDegreesLatitude(), loc.GetDegreesLongitude())
	case *waConsumerApplication.ConsumerApplication_Content_ContactMessage:
		c.kind = "contact"
		c.text = contactText(v.ContactMessage)
	case *waConsumerApplication.ConsumerApplication_Content_ContactsArrayMessage:
		c.kind = "contact"
		var lines []string
		for _, cm := range v.ContactsArrayMessage.GetContacts() {
			if t := contactText(cm); t != "" && t != "Contact" {
				lines = append(lines, t)
			}
		}
		c.text = firstNonEmpty(strings.Join(lines, "\n"), v.ContactsArrayMessage.GetDisplayName(), "Contacts")
	case *waConsumerApplication.ConsumerApplication_Content_GroupInviteMessage:
		g := v.GroupInviteMessage
		c.text = "Lời mời vào nhóm: " + firstNonEmpty(g.GetGroupName(), "một nhóm")
		if cap := e.textOf(chat, g.GetCaption()); cap != "" {
			c.text += "\n" + cap
		}
	case *waConsumerApplication.ConsumerApplication_Content_PollCreationMessage:
		c.text = pollText(v.PollCreationMessage)
	default:
		return []waContent{unsupportedPart(shortTypeName(content.GetContent()))}
	}
	return []waContent{c}
}

// shortTypeName turns "*waConsumerApplication.ConsumerApplication_Content_Foo" into "Foo".
func shortTypeName(v any) string {
	if v == nil {
		return ""
	}
	name := fmt.Sprintf("%T", v)
	if i := strings.LastIndex(name, "_"); i >= 0 {
		name = name[i+1:]
	}
	return strings.TrimPrefix(name, "*")
}

func pollText(p *waConsumerApplication.ConsumerApplication_PollCreationMessage) string {
	var b strings.Builder
	b.WriteString("Poll: " + firstNonEmpty(p.GetName(), "(no question)"))
	for _, o := range p.GetOptions() {
		if n := o.GetOptionName(); n != "" {
			b.WriteString("\n- " + n)
		}
	}
	return b.String()
}

var (
	vcardNameRe  = regexp.MustCompile(`(?mi)^FN[;:][^:\r\n]*:?(.*)$`)
	vcardPhoneRe = regexp.MustCompile(`(?mi)^TEL[^:\r\n]*:(.+)$`)
)

// contactText is the name (and first phone number) of a shared contact.
func contactText(cm *waConsumerApplication.ConsumerApplication_ContactMessage) string {
	dec, err := cm.Decode()
	if err != nil {
		return "Contact"
	}
	name := dec.GetAncillary().GetDisplayName()
	vcard := dec.GetIntegral().GetVcard()
	if name == "" {
		if m := vcardNameRe.FindStringSubmatch(vcard); m != nil {
			name = strings.TrimSpace(m[1])
		}
	}
	phone := ""
	if m := vcardPhoneRe.FindStringSubmatch(vcard); m != nil {
		phone = strings.TrimSpace(m[1])
	}
	return firstNonEmpty(strings.TrimSpace(name+"\n"+phone), "Contact")
}

func (e *e2eeState) extendedText(chat string, et *waConsumerApplication.ConsumerApplication_ExtendedTextMessage) waContent {
	c := waContent{kind: "text"}
	c.text = e.textOf(chat, et.GetText())
	c.text, c.links = previewLinks(c.text, et.GetMatchedText(), et.GetCanonicalURL(), et.GetTitle())
	if c.text == "" {
		c.text = firstNonEmpty(et.GetTitle(), et.GetDescription())
	}
	if c.text != "" && strings.TrimSpace(et.GetCanonicalURL()) != "" && strings.TrimSpace(et.GetText().GetText()) == "" {
		c.kind = "link"
	}
	return c
}

func (e *e2eeState) decodeSticker(msg *waConsumerApplication.ConsumerApplication_StickerMessage) (c waContent) {
	c.kind = "text"
	dec, err := msg.Decode()
	if err != nil {
		c.text = "Nhãn dán (không giải mã được)"
		return c
	}
	c.kind = "sticker"
	// Stickers that live on Meta's servers are resolved through the receiver-fetch lookup
	// ChatSpace always did for them (not a new request); everything else is downloaded
	// from the media transport on demand.
	if id := dec.GetAncillary().GetReceiverFetchID(); id != "" && e.plain != nil && e.ctx != nil {
		ctx, cancel := context.WithTimeout(e.ctx, 15*time.Second)
		defer cancel()
		if media, err := e.plain.FetchReceiverFetchMedia(ctx, id); err == nil {
			c.media = &Media{Mime: media.MimeType, URL: media.URL, Width: media.Width, Height: media.Height}
			return c
		}
	}
	c.media, c.ref = transportInfo(dec.GetIntegral().GetTransport(), "image")
	c.media.Width, c.media.Height = int(dec.GetAncillary().GetWidth()), int(dec.GetAncillary().GetHeight())
	return c
}

func (e *e2eeState) decodeImage(chat string, msg *waConsumerApplication.ConsumerApplication_ImageMessage) (c waContent) {
	c.kind = "image"
	dec, err := msg.Decode()
	if err != nil {
		c.kind, c.text = "text", "Ảnh (không giải mã được)"
		return c
	}
	c.media, c.ref = transportInfo(dec.GetIntegral().GetTransport(), "image")
	c.media.Width, c.media.Height = int(dec.GetAncillary().GetWidth()), int(dec.GetAncillary().GetHeight())
	c.text = e.textOf(chat, msg.GetCaption())
	return c
}

func (e *e2eeState) decodeVideo(chat string, msg *waConsumerApplication.ConsumerApplication_VideoMessage) (c waContent) {
	c.kind = "video"
	dec, err := msg.Decode()
	if err != nil {
		c.kind, c.text = "text", "Video (không giải mã được)"
		return c
	}
	c.media, c.ref = transportInfo(dec.GetIntegral().GetTransport(), "video")
	anc := dec.GetAncillary()
	c.media.Width, c.media.Height = int(anc.GetWidth()), int(anc.GetHeight())
	c.media.Duration = float64(anc.GetSeconds())
	switch {
	case strings.HasPrefix(c.media.Mime, "image/"):
		c.kind = "image" // Facebook sometimes wraps images in video containers
	case anc.GetGifPlayback():
		c.kind = "gif"
	}
	c.text = e.textOf(chat, msg.GetCaption())
	return c
}

// Armadillo content ----------------------------------------------------------------------

func (e *e2eeState) armadilloParts(chat, who string, app *waArmadilloApplication.Armadillo) []waContent {
	switch p := app.GetPayload().GetPayload().(type) {
	case *waArmadilloApplication.Armadillo_Payload_Content:
		return e.armadilloContent(chat, who, p.Content)
	case *waArmadilloApplication.Armadillo_Payload_ApplicationData:
		if ai := p.ApplicationData.GetAiBotResponse(); ai.GetMessageText() != "" {
			return []waContent{{kind: "text", text: ai.GetMessageText()}}
		}
		// metadata sync (read / archive / delete-for-me of another device), history documents
	}
	// Signal (backup secrets) and sub-protocol payloads
	return nil
}

func (e *e2eeState) armadilloContent(chat, who string, content *waArmadilloApplication.Armadillo_Content) []waContent {
	switch content.GetContent().(type) {
	case *waArmadilloApplication.Armadillo_Content_CommonSticker_:
		return []waContent{{kind: "text", text: "\U0001F44D"}} // the Messenger "like" button
	case *waArmadilloApplication.Armadillo_Content_ScreenshotAction_:
		what := "đã chụp màn hình"
		if content.GetScreenshotAction().GetScreenshotType() == waArmadilloApplication.Armadillo_Content_ScreenshotAction_SCREEN_RECORDING {
			what = "đã quay màn hình"
		}
		return []waContent{{kind: "system", text: sentence(who, what)}}
	case *waArmadilloApplication.Armadillo_Content_ExtendedContentMessage:
		return e.xmaParts(chat, who, content.GetExtendedContentMessage())
	case *waArmadilloApplication.Armadillo_Content_RavenMessage_:
		return e.ravenParts(content.GetRavenMessage())
	case *waArmadilloApplication.Armadillo_Content_RavenMessageMsgr:
		return e.ravenParts(content.GetRavenMessageMsgr())
	case *waArmadilloApplication.Armadillo_Content_RavenActionNotifMessage_:
		return nil // "played" / "screenshot" notices about a disappearing photo
	case *waArmadilloApplication.Armadillo_Content_NetworkVerificationMessage_:
		return nil
	case *waArmadilloApplication.Armadillo_Content_ImageGalleryMessage_:
		images, err := content.GetImageGalleryMessage().Decode()
		if err != nil {
			return []waContent{unsupportedPart("Album ảnh")}
		}
		parts := make([]waContent, 0, len(images))
		for _, img := range images {
			c := waContent{kind: "image"}
			c.media, c.ref = transportInfo(img.GetIntegral().GetTransport(), "image")
			c.media.Width, c.media.Height = int(img.GetAncillary().GetWidth()), int(img.GetAncillary().GetHeight())
			parts = append(parts, c)
		}
		return parts
	case *waArmadilloApplication.Armadillo_Content_PaymentsTransactionMessage_:
		pm := content.GetPaymentsTransactionMessage()
		what := "Thanh toán"
		if amount := strings.TrimSpace(pm.GetAmount() + " " + pm.GetCurrency()); amount != "" {
			what += " " + amount
		}
		return []waContent{unsupportedPart(what)}
	case *waArmadilloApplication.Armadillo_Content_BumpExistingMessage_:
		return []waContent{{kind: "system", text: sentence(who, "đã đẩy một tin nhắn lên")}}
	case *waArmadilloApplication.Armadillo_Content_NoteReplyMessage_:
		nr := content.GetNoteReplyMessage()
		c := waContent{kind: "text"}
		c.text = "Đã trả lời một ghi chú"
		if note := nr.GetNoteText().GetText(); note != "" {
			c.text += ": " + note
		}
		if reply := e.textOf(chat, nr.GetTextContent()); reply != "" {
			c.text += "\n" + reply
		}
		return []waContent{c}
	case *waArmadilloApplication.Armadillo_Content_ExtendedMessageContentWithSear:
		if u := httpURL(content.GetExtendedMessageContentWithSear().GetNativeURL()); u != "" {
			return []waContent{{kind: "link", text: u}}
		}
		return []waContent{unsupportedPart("Nội dung được chia sẻ")}
	}
	return []waContent{unsupportedPart(shortTypeName(content.GetContent()))}
}

// sentence builds "Name did something" for system lines.
func sentence(who, what string) string {
	if who == "" {
		who = "Ai đó"
	}
	return who + " " + what
}

func (e *e2eeState) ravenParts(rm *waArmadilloApplication.Armadillo_Content_RavenMessage) []waContent {
	switch rm.GetMediaContent().(type) {
	case *waArmadilloApplication.Armadillo_Content_RavenMessage_ImageMessage:
		img, err := rm.DecodeImageMessage()
		if err != nil {
			return []waContent{unsupportedPart("Ảnh tự huỷ")}
		}
		c := waContent{kind: "image"}
		c.media, c.ref = transportInfo(img.GetIntegral().GetTransport(), "image")
		c.media.Width, c.media.Height = int(img.GetAncillary().GetWidth()), int(img.GetAncillary().GetHeight())
		return []waContent{c}
	case *waArmadilloApplication.Armadillo_Content_RavenMessage_VideoMessage:
		vid, err := rm.DecodeVideoMessage()
		if err != nil {
			return []waContent{unsupportedPart("Video tự huỷ")}
		}
		c := waContent{kind: "video"}
		c.media, c.ref = transportInfo(vid.GetIntegral().GetTransport(), "video")
		c.media.Width, c.media.Height = int(vid.GetAncillary().GetWidth()), int(vid.GetAncillary().GetHeight())
		c.media.Duration = float64(vid.GetAncillary().GetSeconds())
		return []waContent{c}
	}
	return []waContent{unsupportedPart("Tin nhắn tự huỷ")}
}

var xmaCallLabels = map[waArmadilloXMA.ExtendedContentMessage_ExtendedContentType]string{
	waArmadilloXMA.ExtendedContentMessage_RTC_AUDIO_CALL:              "Cuộc gọi thoại",
	waArmadilloXMA.ExtendedContentMessage_RTC_VIDEO_CALL:              "Cuộc gọi video",
	waArmadilloXMA.ExtendedContentMessage_RTC_MISSED_AUDIO_CALL:       "Cuộc gọi thoại bị nhỡ",
	waArmadilloXMA.ExtendedContentMessage_RTC_MISSED_VIDEO_CALL:       "Cuộc gọi video bị nhỡ",
	waArmadilloXMA.ExtendedContentMessage_RTC_GROUP_AUDIO_CALL:        "Cuộc gọi thoại nhóm",
	waArmadilloXMA.ExtendedContentMessage_RTC_GROUP_VIDEO_CALL:        "Cuộc gọi video nhóm",
	waArmadilloXMA.ExtendedContentMessage_RTC_MISSED_GROUP_AUDIO_CALL: "Cuộc gọi thoại nhóm bị nhỡ",
	waArmadilloXMA.ExtendedContentMessage_RTC_MISSED_GROUP_VIDEO_CALL: "Cuộc gọi video nhóm bị nhỡ",
	waArmadilloXMA.ExtendedContentMessage_RTC_ONGOING_AUDIO_CALL:      "Cuộc gọi thoại đang diễn ra",
	waArmadilloXMA.ExtendedContentMessage_RTC_ONGOING_VIDEO_CALL:      "Cuộc gọi video đang diễn ra",
}

// xmaParts maps an Armadillo ExtendedContentMessage ("XMA": a card with title, subtitle
// and call-to-action buttons) by what it stands for.
func (e *e2eeState) xmaParts(chat, who string, x *waArmadilloXMA.ExtendedContentMessage) []waContent {
	tt := x.GetTargetType()
	title, sub := strings.TrimSpace(x.GetTitleText()), strings.TrimSpace(x.GetSubtitleText())

	if label, ok := xmaCallLabels[tt]; ok {
		text := label
		if sub != "" {
			text += " \u00B7 " + sub
		}
		return []waContent{{kind: "call", text: text}}
	}
	switch tt {
	case waArmadilloXMA.ExtendedContentMessage_MSG_LOCATION_SHARING, waArmadilloXMA.ExtendedContentMessage_MSG_LOCATION_SHARING_V2:
		c := waContent{kind: "location", text: firstNonEmpty(strings.TrimSpace(title+"\n"+sub), "Location")}
		for _, cta := range x.GetCtas() {
			if lat, lon, ok := locationFromNativeURL(cta.GetNativeURL()); ok {
				c.links = mapLinks(c.text, lat, lon)
				break
			}
		}
		return []waContent{c}
	case waArmadilloXMA.ExtendedContentMessage_MSG_CONTACT, waArmadilloXMA.ExtendedContentMessage_MSG_PHONE_NUMBER:
		return []waContent{{kind: "contact", text: firstNonEmpty(strings.TrimSpace(title+"\n"+sub), "Contact")}}
	case waArmadilloXMA.ExtendedContentMessage_FB_STORY_REPLY, waArmadilloXMA.ExtendedContentMessage_FB_PRODUCER_STORY_REPLY,
		waArmadilloXMA.ExtendedContentMessage_FB_QUICKSNAP_REPLY, waArmadilloXMA.ExtendedContentMessage_IG_STORY_REPLY,
		waArmadilloXMA.ExtendedContentMessage_IG_STORY_HIGHLIGHT_REPLY:
		if parts := e.associatedParts(chat, x); len(parts) > 0 {
			label := "Đã trả lời một tin"
			if tt == waArmadilloXMA.ExtendedContentMessage_FB_QUICKSNAP_REPLY {
				label = "Đã trả lời một quicksnap"
			}
			prefixText(&parts[0], label)
			return parts
		}
	}

	// Everything else is a card: the text the sender typed, the title of what was shared
	// and the address it opens.
	body := resolveE2EEMentions(strings.TrimSpace(x.GetMessageText()), x.GetMentionedJID(), x.GetMentions(), func(u string) string { return e.mentionName(chat, u) })
	link := ""
	for _, cta := range x.GetCtas() {
		if link = firstNonEmpty(httpURL(cta.GetNativeURL()), httpURL(cta.GetActionURL())); link != "" {
			break
		}
	}
	text, links := cardText(body, title, link)
	c := waContent{kind: "text", text: text, links: links}
	if link != "" {
		c.kind = "link"
	}
	if text == "" {
		return []waContent{unsupportedPart(xmaLabel(tt))}
	}
	for _, raw := range x.GetPreviews() {
		img, err := armadilloutil.Unmarshal(&waMediaTransport.ImageTransport{}, raw, 2)
		if err != nil || img.GetIntegral().GetTransport().GetIntegral().GetDirectPath() == "" {
			continue
		}
		c.media, c.ref = transportInfo(img.GetIntegral().GetTransport(), "image")
		c.media.Width, c.media.Height = int(img.GetAncillary().GetWidth()), int(img.GetAncillary().GetHeight())
		break
	}
	return []waContent{c}
}

// xmaLabel is a short human name of an XMA target for the "unsupported" sentence.
func xmaLabel(tt waArmadilloXMA.ExtendedContentMessage_ExtendedContentType) string {
	switch {
	case tt == waArmadilloXMA.ExtendedContentMessage_MSG_P2P_PAYMENT || tt == waArmadilloXMA.ExtendedContentMessage_MSG_OPG_P2P_PAYMENT:
		return "Payment"
	case strings.HasPrefix(tt.String(), "IG_"):
		return "Bài chia sẻ từ Instagram"
	case strings.HasPrefix(tt.String(), "FB_"):
		return "Bài chia sẻ từ Facebook"
	}
	return "Nội dung được chia sẻ"
}

// associatedParts decodes the message a story reply / mention card wraps.
func (e *e2eeState) associatedParts(chat string, x *waArmadilloXMA.ExtendedContentMessage) []waContent {
	if x.GetAssociatedMessage() == nil {
		return nil
	}
	var assoc waMsgApplication.MessageApplication
	if _, err := armadilloutil.Unmarshal(&assoc, x.GetAssociatedMessage(), 2); err != nil {
		return nil
	}
	var cons waConsumerApplication.ConsumerApplication
	if _, err := armadilloutil.Unmarshal(&cons, assoc.GetPayload().GetSubProtocol().GetConsumerMessage(), 1); err != nil {
		return nil
	}
	content := cons.GetPayload().GetContent()
	if content == nil {
		return nil
	}
	switch content.GetContent().(type) {
	case *waConsumerApplication.ConsumerApplication_Content_ReactionMessage,
		*waConsumerApplication.ConsumerApplication_Content_EditMessage,
		*waConsumerApplication.ConsumerApplication_Content_PollUpdateMessage:
		return nil
	}
	return e.consumerParts(chat, content)
}

// prefixText puts a label line before a part's text, shifting its link offsets.
func prefixText(c *waContent, label string) {
	if c.text == "" {
		c.text = label
		return
	}
	c.text = label + ": " + c.text
	shift := utf16Len(label + ": ")
	for i := range c.links {
		c.links[i].Offset += shift
	}
}

// cardText lays out a link card: what the sender typed, then the title of the page. When
// the address is not already written in the text, the title (or, lacking one, the whole
// text) links to it; with nothing else to show the address itself is the text.
func cardText(body, title, link string) (string, []Link) {
	text := body
	titleOff := -1
	if title != "" && !strings.Contains(body, title) {
		if text == "" {
			titleOff, text = 0, title
		} else {
			text += "\n\n"
			titleOff = utf16Len(text)
			text += title
		}
	}
	if link == "" || strings.Contains(text, link) {
		return text, nil
	}
	if text == "" {
		return link, nil
	}
	if titleOff >= 0 {
		return text, []Link{{Offset: titleOff, Length: utf16Len(title), URL: link}}
	}
	return text, []Link{{Offset: 0, Length: utf16Len(text), URL: link}}
}

// Links and offsets ---------------------------------------------------------------------

func utf16Len(s string) int { return len(utf16.Encode([]rune(s))) }

// utf16Offset is where sub starts in s in UTF-16 code units, -1 when absent.
func utf16Offset(s, sub string) int {
	i := strings.Index(s, sub)
	if i < 0 {
		return -1
	}
	return utf16Len(s[:i])
}

func httpURL(raw string) string {
	u, err := url.Parse(strings.TrimSpace(raw))
	if err != nil || (u.Scheme != "https" && u.Scheme != "http") || u.Host == "" {
		return ""
	}
	return u.String()
}

func normURL(s string) string {
	s = strings.ToLower(strings.TrimSpace(s))
	s = strings.TrimPrefix(strings.TrimPrefix(s, "https://"), "http://")
	s = strings.TrimPrefix(s, "www.")
	return strings.TrimSuffix(s, "/")
}

// previewLinks makes a text message with a link preview (ExtendedTextMessage) openable
// when its text does not carry the preview's address in a form the app can find itself:
//
//   - the matched text is in the text but is a short or scheme-less form of the address
//     (or a different address altogether): that span links to the address;
//   - the address is not in the text at all: the whole text links to it;
//   - there is no text: the title (or the address) is shown and linked.
func previewLinks(text, matched, canonical, title string) (string, []Link) {
	link := httpURL(canonical)
	if link == "" && matched != "" {
		link = httpURL(matched)
		if link == "" && !strings.ContainsAny(matched, " \n") && strings.Contains(matched, ".") {
			link = httpURL("https://" + matched)
		}
	}
	if link == "" {
		return text, nil
	}
	if strings.TrimSpace(text) == "" {
		if title == "" {
			return link, nil
		}
		return title, []Link{{Offset: 0, Length: utf16Len(title), URL: link}}
	}
	if matched != "" {
		if off := utf16Offset(text, matched); off >= 0 {
			findable := strings.Contains(matched, "://") || strings.HasPrefix(strings.ToLower(matched), "www.")
			if findable && normURL(matched) == normURL(link) {
				return text, nil
			}
			if findable && strings.Contains(link, matched) {
				return text, nil
			}
			return text, []Link{{Offset: off, Length: utf16Len(matched), URL: link}}
		}
	}
	if strings.Contains(text, link) || strings.Contains(strings.ToLower(text), normURL(link)) {
		return text, nil
	}
	return text, []Link{{Offset: 0, Length: utf16Len(text), URL: link}}
}

// mapLinks makes a location message open in a map.
func mapLinks(text string, lat, lon float64) []Link {
	if text == "" || (lat == 0 && lon == 0) || lat < -90 || lat > 90 || lon < -180 || lon > 180 {
		return nil
	}
	return []Link{{Offset: 0, Length: utf16Len(text), URL: fmt.Sprintf("https://www.google.com/maps?q=%s,%s",
		strconv.FormatFloat(lat, 'f', 6, 64), strconv.FormatFloat(lon, 'f', 6, 64))}}
}

// locationFromNativeURL reads lat/long from "messenger://location_share?lat=..&long=..".
func locationFromNativeURL(raw string) (lat, lon float64, ok bool) {
	u, err := url.Parse(raw)
	if err != nil || u.Scheme != "messenger" || u.Host != "location_share" {
		return 0, 0, false
	}
	la, err1 := strconv.ParseFloat(u.Query().Get("lat"), 64)
	lo, err2 := strconv.ParseFloat(u.Query().Get("long"), 64)
	if err1 != nil || err2 != nil {
		return 0, 0, false
	}
	return la, lo, true
}
