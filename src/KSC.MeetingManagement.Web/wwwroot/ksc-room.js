// Browser side of the meeting room: device preview and the LiveKit connection. The library is bundled (lib/livekit-client),
// nothing is loaded from the internet. This file only talks to the DOM and reports state back to the Blazor page.
import { AudioPresets, Room, RoomEvent, ScreenSharePresets, Track, ConnectionState, VideoPresets } from "./lib/livekit-client/livekit-client.esm.mjs";

let previewStream = null;
let room = null;
let dotnet = null;
let tiles = null;
let whiteboardCanvas = null;
let presenterIdentity = "";
let localIsPresenter = false;
let whiteboardState = [];
let whiteboardActive = false;
let whiteboardTool = "pen";
let whiteboardDraft = null;
let recordingActive = false;
const whiteboardTopic = "ksc-whiteboard-v1";
const screenShareTopic = "ksc-screen-share-v1";

export function isSupported() {
    return !!(navigator.mediaDevices && navigator.mediaDevices.getUserMedia && window.RTCPeerConnection);
}

/** Opens camera and microphone only to show a self view before joining. Returns an error code or "ok". */
export async function startPreview(videoElement, wantVideo, wantAudio) {
    await stopPreview();
    try {
        previewStream = await navigator.mediaDevices.getUserMedia({
            video: wantVideo ? { width: { ideal: 1920 }, height: { ideal: 1080 }, frameRate: { ideal: 30, max: 30 } } : false,
            audio: wantAudio
        });
        if (videoElement) {
            videoElement.srcObject = previewStream;
            videoElement.muted = true;
            await videoElement.play().catch(() => { });
        }
        return "ok";
    } catch (e) {
        return e && e.name ? e.name : "error"; // NotAllowedError, NotFoundError, NotReadableError, ...
    }
}

export async function stopPreview() {
    if (previewStream) {
        previewStream.getTracks().forEach(t => t.stop());
        previewStream = null;
    }
}

/** Connects with the token issued by the API. mic/camera tell whether to publish them right away. */
export async function connect(serverUrl, token, iceServers, container, boardCanvas, reference, mic, camera, organizerIdentity, isOrganizer = false) {
    await disconnect();
    await stopPreview();
    dotnet = reference;
    tiles = container;
    whiteboardCanvas = boardCanvas;
    presenterIdentity = organizerIdentity || "";
    localIsPresenter = !!isOrganizer;
    room = new Room({
        adaptiveStream: true,
        dynacast: true,
        videoCaptureDefaults: { resolution: VideoPresets.h1080.resolution },
        publishDefaults: {
            audioPreset: AudioPresets.speech,
            videoEncoding: { maxBitrate: 3000000, maxFramerate: 30 },
            videoSimulcastLayers: [VideoPresets.h360, VideoPresets.h720],
            screenShareEncoding: ScreenSharePresets.h1080fps15.encoding,
            screenShareSimulcastLayers: [ScreenSharePresets.h720fps15, ScreenSharePresets.h360fps15]
        }
    });

    room.on(RoomEvent.TrackSubscribed, (track, _publication, participant) => addTile(track, participant));
    room.on(RoomEvent.TrackUnsubscribed, (track) => removeTile(track.sid));
    room.on(RoomEvent.DataReceived, receiveWhiteboardData);
    room.on(RoomEvent.DataReceived, receiveScreenShareData);
    room.on(RoomEvent.LocalTrackPublished, (publication) => {
        if (publication.track && publication.track.kind === Track.Kind.Video && [Track.Source.Camera, Track.Source.ScreenShare].includes(publication.source)) {
            addTile(publication.track, room.localParticipant, true);
        }
    });
    room.on(RoomEvent.LocalTrackUnpublished, (publication) => {
        removeTile(publication.trackSid);
        if (publication.source === Track.Source.ScreenShare) screenShareState(false);
    });
    room.on(RoomEvent.ParticipantConnected, participant => {
        reportParticipantMicrophones();
        if (isPresenter() && participant.identity !== room.localParticipant.identity) {
            void sendWhiteboardSnapshot(participant.identity);
            if (recordingActive) void publishRecordingStatus(true, [participant.identity]);
        }
    });
    room.on(RoomEvent.ParticipantDisconnected, reportParticipantMicrophones);
    room.on(RoomEvent.TrackPublished, reportParticipantMicrophones);
    room.on(RoomEvent.TrackUnpublished, reportParticipantMicrophones);
    room.on(RoomEvent.TrackMuted, reportParticipantMicrophones);
    room.on(RoomEvent.TrackUnmuted, reportParticipantMicrophones);
    room.on(RoomEvent.Reconnecting, () => state("reconnecting"));
    room.on(RoomEvent.Reconnected, () => { state("connected"); reportParticipantMicrophones(); });
    room.on(RoomEvent.Disconnected, () => state("disconnected"));
    room.on(RoomEvent.ConnectionQualityChanged, (quality, participant) => {
        if (participant === room?.localParticipant) reportConnectionQuality(quality);
    });

    const configuration = iceServers && iceServers.length
        ? { rtcConfig: { iceServers: iceServers.map(s => ({ urls: s.urls, username: s.username, credential: s.credential })) } }
        : undefined;
    try {
        await room.connect(serverUrl, token, configuration);
    } catch (e) {
        room = null;
        return e && e.message ? e.message : "connect-failed";
    }

    // Use the connected identity as the canonical presenter identity. The API
    // authorizes the host role; relying on a separately formatted identity here
    // could leave the host's canvas inactive and make peers reject its messages.
    if (localIsPresenter) presenterIdentity = room.localParticipant.identity;

    state("connected");
    // Failing to open a device must not block the meeting: the user can still listen and chat.
    await room.localParticipant.setMicrophoneEnabled(!!mic).catch(() => { });
    await room.localParticipant.setCameraEnabled(!!camera).catch(() => { });
    setupWhiteboard();
    if (isPresenter()) {
        for (const participant of room.remoteParticipants.values()) {
            void sendWhiteboardSnapshot(participant.identity);
        }
    }
    reportParticipantMicrophones();
    syncMediaEmptyState();
    return "ok";
}

export async function setMic(on) {
    if (!room) return false;
    await room.localParticipant.setMicrophoneEnabled(on);
    return on;
}

export async function setCamera(on) {
    if (!room) return false;
    await room.localParticipant.setCameraEnabled(on);
    return on;
}

/** Starts or stops screen sharing. Returns whether sharing is on afterwards (the user can cancel the browser picker). */
export async function setScreenShare(on) {
    if (!room) return false;
    if (!isPresenter() && !screenShareApproved) return false;
    try {
        await room.localParticipant.setScreenShareEnabled(on, {
            resolution: ScreenSharePresets.h1080fps15.resolution,
            contentHint: "detail",
            audio: false
        });
    } catch {
        return false;
    }
    screenShareState(on);
    return on;
}

let screenShareApproved = false;

export async function requestScreenShare() {
    if (!room || isPresenter()) return false;
    try {
        const bytes = new TextEncoder().encode(JSON.stringify({ type: "request", name: room.localParticipant.name || room.localParticipant.identity }));
        await room.localParticipant.publishData(bytes, { reliable: true, topic: screenShareTopic, destinationIdentities: [presenterIdentity] });
        return true;
    } catch {
        return false;
    }
}

export async function decideScreenShare(identity, allowed) {
    if (!room || !isPresenter()) return false;
    try {
        const bytes = new TextEncoder().encode(JSON.stringify({ type: "decision", allowed: !!allowed }));
        await room.localParticipant.publishData(bytes, { reliable: true, topic: screenShareTopic, destinationIdentities: [identity] });
        return true;
    } catch {
        return false;
    }
}

export async function setRecordingStatus(active) {
    if (!room || !isPresenter()) return false;
    recordingActive = !!active;
    await publishRecordingStatus(recordingActive);
    return true;
}

async function publishRecordingStatus(active, destinationIdentities) {
    if (!room || !isPresenter()) return;
    const bytes = new TextEncoder().encode(JSON.stringify({ type: "recording", active: !!active }));
    try {
        await room.localParticipant.publishData(bytes, { reliable: true, topic: screenShareTopic, destinationIdentities });
    } catch {
        // Recording itself is controlled by the server; a failed badge broadcast must not alter that state.
    }
}

export async function disconnect() {
    if (room) {
        const old = room;
        room = null;
        await old.disconnect();
    }
    if (tiles) {
        tiles.replaceChildren();
    }
    whiteboardActive = false;
    whiteboardState = [];
    whiteboardDraft = null;
    presenterIdentity = "";
    localIsPresenter = false;
    screenShareApproved = false;
    whiteboardCanvas = null;
}

function reportConnectionQuality(quality) {
    if (dotnet) dotnet.invokeMethodAsync("OnConnectionQuality", quality, "adaptive 1080p");
}

function isPresenter() {
    // The API has already authorized the local participant and the room role is
    // passed by the Blazor page. Do not re-derive authorization from LiveKit's
    // identity string here; an identity-format difference must not disable the
    // organizer's local canvas handlers.
    return !!room && localIsPresenter;
}

function setupWhiteboard(attempt = 0) {
    if (!whiteboardCanvas || !room) return;
    // The canvas is rendered conditionally when the room becomes active. Blazor can
    // finish that render just after the LiveKit connection, so don't bind handlers
    // to an ElementReference that hasn't been attached to the document yet.
    const bounds = whiteboardCanvas.getBoundingClientRect();
    if (!whiteboardCanvas.isConnected || bounds.width === 0 || bounds.height === 0) {
        if (attempt < 30) requestAnimationFrame(() => setupWhiteboard(attempt + 1));
        return;
    }

    whiteboardActive = isPresenter();
    whiteboardCanvas.width = 1600;
    whiteboardCanvas.height = 900;
    for (const video of tiles?.querySelectorAll('video[data-track-source="screen_share"]') ?? []) {
        const tile = video.closest(".ksc-tile-video");
        if (!tile) continue;
        tile.classList.add("ksc-screen-share-on-board");
        whiteboardCanvas.parentElement.appendChild(tile);
        whiteboardCanvas.style.background = "transparent";
    }
    drawWhiteboard();
    whiteboardCanvas.onpointerdown = whiteboardActive ? beginWhiteboardStroke : null;
    whiteboardCanvas.onpointermove = whiteboardActive ? continueWhiteboardStroke : null;
    whiteboardCanvas.onpointerup = whiteboardActive ? finishWhiteboardStroke : null;
    whiteboardCanvas.onpointercancel = whiteboardCanvas.onpointerup;
    whiteboardCanvas.style.touchAction = whiteboardActive ? "none" : "pan-y";
    whiteboardCanvas.style.zIndex = "3";
    whiteboardCanvas.style.cursor = whiteboardTool === "text" ? "text" : "crosshair";
}

export function setWhiteboardText(text) {
    if (!isPresenter() || !whiteboardCanvas || typeof text !== "string") return false;
    const value = text.trim().slice(0, 500);
    if (!value) return false;
    const item = { id: `${Date.now()}-${Math.random().toString(36).slice(2)}`, kind: "text", text: value, x: 0.05, y: 0.05, color: whiteboardCanvas.dataset.penColor || "#17324D" };
    addWhiteboardItem(item);
    drawWhiteboard();
    void publishWhiteboardMessage({ type: "item", item });
    return true;
}

export function setWhiteboardTool(toolOrColor) {
    if (!isPresenter()) return false;
    if (typeof toolOrColor === "string" && /^#[0-9a-f]{6}$/i.test(toolOrColor)) {
        whiteboardCanvas?.setAttribute("data-pen-color", toolOrColor);
        return true;
    }
    const tools = ["pen", "text", "line", "rectangle", "circle", "triangle", "arrow"];
    if (!tools.includes(toolOrColor)) return false;
    whiteboardTool = toolOrColor;
    if (whiteboardCanvas) whiteboardCanvas.style.cursor = toolOrColor === "text" ? "text" : "crosshair";
    return true;
}

export async function clearWhiteboard() {
    if (!isPresenter()) return false;
    whiteboardState = [];
    drawWhiteboard();
    await publishWhiteboardMessage({ type: "clear" });
    return true;
}

export async function undoWhiteboard() {
    if (!isPresenter() || !whiteboardState.length) return false;
    whiteboardState.pop();
    drawWhiteboard();
    await publishWhiteboardMessage({ type: "undo" });
    return true;
}

async function sendWhiteboardSnapshot(identity) {
    await publishWhiteboardMessage({ type: "snapshot-start" }, [identity]);
    for (const item of whiteboardState) {
        if (item.kind === "pen") await publishWhiteboardMessage({ type: "stroke", stroke: item }, [identity]);
        else await publishWhiteboardMessage({ type: "item", item }, [identity]);
    }
}

function whiteboardPoint(event) {
    const rect = whiteboardCanvas.getBoundingClientRect();
    return {
        x: Math.max(0, Math.min(1, (event.clientX - rect.left) / rect.width)),
        y: Math.max(0, Math.min(1, (event.clientY - rect.top) / rect.height))
    };
}

function beginWhiteboardStroke(event) {
    if (!whiteboardActive || event.button !== 0) return;
    event.preventDefault();
    const point = whiteboardPoint(event);
    const color = whiteboardCanvas.dataset.penColor || "#17324D";
    if (whiteboardTool === "text") {
        openWhiteboardTextEditor(point, color);
        return;
    }
    whiteboardCanvas.setPointerCapture(event.pointerId);
    if (whiteboardState.length >= 300) whiteboardState.shift();
    if (whiteboardTool !== "pen") {
        whiteboardDraft = { id: `${Date.now()}-${Math.random().toString(36).slice(2)}`, kind: "shape", shape: whiteboardTool, color, x: point.x, y: point.y, w: 0, h: 0 };
        return;
    }
    const stroke = { id: `${Date.now()}-${Math.random().toString(36).slice(2)}`, kind: "pen", color, points: [point] };
    whiteboardState.push(stroke);
    void publishWhiteboardMessage({ type: "stroke-start", stroke: { ...stroke, points: stroke.points.slice() } });
    drawWhiteboard();
}

function openWhiteboardTextEditor(point, color) {
    const stage = whiteboardCanvas?.parentElement;
    if (!stage) return;
    stage.querySelector(".ksc-whiteboard-text-editor")?.remove();
    const rect = whiteboardCanvas.getBoundingClientRect();
    const editor = document.createElement("textarea");
    editor.className = "ksc-whiteboard-text-editor";
    editor.setAttribute("aria-label", "نوشتن متن روی وایت‌برد");
    editor.placeholder = "اینجا بنویسید…";
    editor.dir = "auto";
    editor.maxLength = 500;
    editor.style.left = `${point.x * rect.width}px`;
    editor.style.top = `${point.y * rect.height}px`;
    stage.appendChild(editor);
    editor.focus();
    let finished = false;
    const finish = commit => {
        if (finished) return;
        finished = true;
        const text = editor.value.trim();
        editor.remove();
        if (!commit || !text) return;
        const item = { id: `${Date.now()}-${Math.random().toString(36).slice(2)}`, kind: "text", text, x: point.x, y: point.y, color };
        addWhiteboardItem(item);
        drawWhiteboard();
        void publishWhiteboardMessage({ type: "item", item });
    };
    editor.addEventListener("keydown", event => {
        if (event.key === "Escape") { event.preventDefault(); finish(false); }
        else if (event.key === "Enter" && (event.ctrlKey || event.metaKey)) { event.preventDefault(); finish(true); }
    });
    editor.addEventListener("blur", () => finish(true), { once: true });
}

function continueWhiteboardStroke(event) {
    if (!whiteboardActive || !whiteboardCanvas.hasPointerCapture(event.pointerId)) return;
    if (whiteboardDraft) {
        const point = whiteboardPoint(event);
        whiteboardDraft.w = point.x - whiteboardDraft.x;
        whiteboardDraft.h = point.y - whiteboardDraft.y;
        drawWhiteboard();
        return;
    }
    const stroke = whiteboardState[whiteboardState.length - 1];
    if (!stroke || stroke.points.length >= 180) return;
    const point = whiteboardPoint(event);
    const previous = stroke.points[stroke.points.length - 1];
    if (Math.hypot(point.x - previous.x, point.y - previous.y) < 0.005) return;
    stroke.points.push(point);
    if (stroke.points.length % 8 === 0) publishStrokePoints(stroke, stroke.points.slice(-8));
    drawWhiteboard();
}

function finishWhiteboardStroke(event) {
    if (!whiteboardActive || !whiteboardCanvas.hasPointerCapture(event.pointerId)) return;
    whiteboardCanvas.releasePointerCapture(event.pointerId);
    if (whiteboardDraft) {
        const item = whiteboardDraft;
        whiteboardDraft = null;
        if (Math.abs(item.w) + Math.abs(item.h) > 0.003) {
            addWhiteboardItem(item);
            void publishWhiteboardMessage({ type: "item", item });
        }
        drawWhiteboard();
        return;
    }
    const stroke = whiteboardState[whiteboardState.length - 1];
    if (stroke && stroke.points.length % 8 !== 0) void publishStrokePoints(stroke, stroke.points.slice(stroke.points.length - (stroke.points.length % 8)));
}

function publishStrokePoints(stroke, points) {
    return publishWhiteboardMessage({ type: "stroke-append", id: stroke.id, points });
}

function drawWhiteboard() {
    if (!whiteboardCanvas) return;
    const context = whiteboardCanvas.getContext("2d", { alpha: true });
    if (!context) return;
    const width = whiteboardCanvas.width;
    const height = whiteboardCanvas.height;
    context.clearRect(0, 0, width, height);
    const hasScreenShare = !!whiteboardCanvas.parentElement?.querySelector(".ksc-screen-share-on-board");
    if (!hasScreenShare) {
        context.fillStyle = "#ffffff";
        context.fillRect(0, 0, width, height);
    }
    context.lineCap = "round";
    context.lineJoin = "round";
    context.lineWidth = 5;
    for (const item of whiteboardState) drawWhiteboardItem(context, item, width, height);
    if (whiteboardDraft) drawWhiteboardItem(context, whiteboardDraft, width, height);
}

function drawWhiteboardItem(context, item, width, height) {
    context.strokeStyle = item.color || "#17324D";
    context.fillStyle = item.color || "#17324D";
    context.lineWidth = 5;
    if (item.kind === "text") {
        context.font = "32px Vazirmatn, sans-serif";
        context.textBaseline = "top";
        context.direction = "rtl";
        context.fillText(item.text || "", item.x * width, item.y * height, width * 0.45);
        return;
    }
    if (item.kind === "shape") {
        const x = item.x * width;
        const y = item.y * height;
        const w = item.w * width;
        const h = item.h * height;
        context.beginPath();
        if (item.shape === "rectangle") context.rect(x, y, w, h);
        else if (item.shape === "circle") context.ellipse(x + w / 2, y + h / 2, Math.abs(w / 2), Math.abs(h / 2), 0, 0, Math.PI * 2);
        else if (whiteboardTool === "triangle" || item.shape === "triangle") {
            context.moveTo(x + w / 2, y);
            context.lineTo(x + w, y + h);
            context.lineTo(x, y + h);
            context.closePath();
        } else if (item.shape === "arrow") {
            context.moveTo(x, y);
            context.lineTo(x + w, y + h);
            const angle = Math.atan2(h, w);
            const size = 18;
            context.lineTo(x + w - size * Math.cos(angle - Math.PI / 6), y + h - size * Math.sin(angle - Math.PI / 6));
            context.moveTo(x + w, y + h);
            context.lineTo(x + w - size * Math.cos(angle + Math.PI / 6), y + h - size * Math.sin(angle + Math.PI / 6));
        } else {
            context.moveTo(x, y);
            context.lineTo(x + w, y + h);
        }
        context.stroke();
        return;
    }
    if (!item.points?.length) return;
    context.beginPath();
    context.moveTo(item.points[0].x * width, item.points[0].y * height);
    for (const point of item.points.slice(1)) context.lineTo(point.x * width, point.y * height);
    if (item.points.length === 1) context.lineTo(item.points[0].x * width + 0.1, item.points[0].y * height + 0.1);
    context.stroke();
}

function addWhiteboardItem(item) {
    if (whiteboardState.length >= 300) whiteboardState.shift();
    whiteboardState.push(item);
}

function receiveWhiteboardData(payload, participant, _kind, topic) {
    if (topic !== whiteboardTopic || !participant || participant.identity !== presenterIdentity) return;
    try {
        const message = JSON.parse(new TextDecoder().decode(payload));
        if (message.type === "snapshot-start") whiteboardState = [];
        else if (message.type === "stroke-start" && message.stroke?.points?.length) {
            addWhiteboardItem({ ...message.stroke, kind: "pen" });
        }
        else if (message.type === "stroke-append" && Array.isArray(message.points)) {
            const stroke = whiteboardState.find(item => item.id === message.id);
            if (stroke) stroke.points.push(...message.points);
        }
        else if (message.type === "stroke" && message.stroke?.points?.length) addWhiteboardItem({ ...message.stroke, kind: "pen" });
        else if (message.type === "item" && message.item) addWhiteboardItem(message.item);
        else if (message.type === "undo") whiteboardState.pop();
        else if (message.type === "clear") whiteboardState = [];
        drawWhiteboard();
    } catch {
        // Ignore malformed or unsupported whiteboard messages.
    }
}

function receiveScreenShareData(payload, participant, _kind, topic) {
    if (topic !== screenShareTopic || !participant) return;
    try {
        const message = JSON.parse(new TextDecoder().decode(payload));
        if (isPresenter() && message.type === "request") {
            dotnet?.invokeMethodAsync("OnScreenShareRequest", participant.identity, participant.name || participant.identity);
        } else if (participant.identity === presenterIdentity && message.type === "decision") {
            screenShareApproved = !!message.allowed;
            dotnet?.invokeMethodAsync("OnScreenShareDecision", screenShareApproved);
        } else if (participant.identity === presenterIdentity && message.type === "recording") {
            dotnet?.invokeMethodAsync("OnRecordingStatus", !!message.active);
        }
    } catch {
        // Ignore malformed screen-share request messages.
    }
}

async function publishWhiteboardMessage(message, destinationIdentities) {
    if (!isPresenter()) return;
    const bytes = new TextEncoder().encode(JSON.stringify(message));
    try {
        await room.localParticipant.publishData(bytes, { reliable: true, topic: whiteboardTopic, destinationIdentities });
    } catch {
        // LiveKit can reject data while reconnecting; the board remains usable locally.
    }
}

export function connectionState() {
    return room ? room.state : ConnectionState.Disconnected;
}

function state(value) {
    if (dotnet) dotnet.invokeMethodAsync("OnRoomState", value);
}

function screenShareState(on) {
    if (dotnet) dotnet.invokeMethodAsync("OnScreenShare", on);
}

function reportParticipantMicrophones() {
    if (!dotnet || !room) return;
    const participants = [room.localParticipant, ...room.remoteParticipants.values()].map(participant => ({
        identity: participant.identity,
        name: participant.name || participant.identity,
        isLocal: participant === room.localParticipant,
        isMicrophoneEnabled: participant.getTrackPublication(Track.Source.Microphone)?.isMuted === false
    }));
    dotnet.invokeMethodAsync("OnParticipants", participants);
}

function addTile(track, participant, local = false) {
    if (!tiles && !(track.source === Track.Source.ScreenShare && whiteboardCanvas?.parentElement)) return;
    if (track.kind === Track.Kind.Audio) {
        if (local) return; // never play our own microphone back
        const audio = track.attach();
        audio.dataset.sid = track.sid;
        tiles?.appendChild(audio);
        return;
    }

    const tile = document.createElement("figure");
    tile.className = "ksc-tile-video";
    tile.dataset.sid = track.sid;
    const video = track.attach();
    video.setAttribute("playsinline", "");
    if (track.source === Track.Source.ScreenShare) {
        video.dataset.trackSource = "screen_share";
        video.addEventListener("loadedmetadata", () => {
            if (video.videoWidth && video.videoHeight) {
                tile.style.aspectRatio = `${video.videoWidth} / ${video.videoHeight}`;
            }
        }, { once: true });
    } else if (track.source === Track.Source.Camera) {
        video.dataset.trackSource = "camera";
    }
    if (local) video.muted = true;
    const caption = document.createElement("figcaption");
    caption.textContent = (participant && (participant.name || participant.identity)) || "";
    tile.append(video, caption);
    if (track.source === Track.Source.ScreenShare) {
        const expand = document.createElement("button");
        expand.type = "button";
        expand.className = "ksc-screen-share-expand";
        expand.textContent = "تمام‌صفحه";
        expand.setAttribute("aria-label", "نمایش اشتراک صفحه به‌صورت تمام‌صفحه");
        expand.addEventListener("click", async () => {
            try {
                if (document.fullscreenElement === tile) await document.exitFullscreen();
                else await tile.requestFullscreen();
            } catch {
                // Fullscreen can be unavailable in embedded or restricted browsers.
            }
        });
        tile.appendChild(expand);
    }
    if (track.source === Track.Source.ScreenShare && whiteboardCanvas?.parentElement) {
        tile.classList.add("ksc-screen-share-on-board");
        whiteboardCanvas.parentElement.appendChild(tile);
        whiteboardCanvas.style.background = "transparent";
        drawWhiteboard();
    } else {
        tiles?.appendChild(tile);
    }
    syncMediaEmptyState();
}

function removeTile(sid) {
    if (!sid) return;
    tiles?.querySelectorAll(`[data-sid="${CSS.escape(sid)}"]`).forEach(el => el.remove());
    whiteboardCanvas?.parentElement?.querySelectorAll(`[data-sid="${CSS.escape(sid)}"]`).forEach(el => el.remove());
    if (!whiteboardCanvas?.parentElement?.querySelector(".ksc-screen-share-on-board") && whiteboardCanvas) {
        whiteboardCanvas.style.background = "#fff";
        drawWhiteboard();
    }
    syncMediaEmptyState();
}

function syncMediaEmptyState() {
    if (!tiles) return;
    const empty = tiles.querySelector(".ksc-room-empty");
    if (empty) empty.hidden = tiles.querySelectorAll(".ksc-tile-video").length > 0
        || !!whiteboardCanvas?.parentElement?.querySelector(".ksc-screen-share-on-board");
}
