// Browser side of the meeting room: device preview and the LiveKit connection. The library is bundled (lib/livekit-client),
// nothing is loaded from the internet. This file only talks to the DOM and reports state back to the Blazor page.
import { Room, RoomEvent, Track, ConnectionState } from "./lib/livekit-client/livekit-client.esm.mjs";

let previewStream = null;
let room = null;
let dotnet = null;
let tiles = null;

export function isSupported() {
    return !!(navigator.mediaDevices && navigator.mediaDevices.getUserMedia && window.RTCPeerConnection);
}

/** Opens camera and microphone only to show a self view before joining. Returns an error code or "ok". */
export async function startPreview(videoElement, wantVideo, wantAudio) {
    await stopPreview();
    try {
        previewStream = await navigator.mediaDevices.getUserMedia({ video: wantVideo, audio: wantAudio });
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
export async function connect(serverUrl, token, iceServers, container, reference, mic, camera) {
    await disconnect();
    await stopPreview();
    dotnet = reference;
    tiles = container;
    room = new Room({ adaptiveStream: true, dynacast: true });

    room.on(RoomEvent.TrackSubscribed, (track, _publication, participant) => addTile(track, participant));
    room.on(RoomEvent.TrackUnsubscribed, (track) => removeTile(track.sid));
    room.on(RoomEvent.LocalTrackPublished, (publication) => {
        if (publication.track && publication.track.kind === Track.Kind.Video && publication.source === Track.Source.Camera) {
            addTile(publication.track, room.localParticipant, true);
        }
    });
    room.on(RoomEvent.LocalTrackUnpublished, (publication) => removeTile(publication.trackSid));
    room.on(RoomEvent.ParticipantConnected, report);
    room.on(RoomEvent.ParticipantDisconnected, report);
    room.on(RoomEvent.Reconnecting, () => state("reconnecting"));
    room.on(RoomEvent.Reconnected, () => { state("connected"); report(); });
    room.on(RoomEvent.Disconnected, () => state("disconnected"));

    const configuration = iceServers && iceServers.length
        ? { rtcConfig: { iceServers: iceServers.map(s => ({ urls: s.urls, username: s.username, credential: s.credential })) } }
        : undefined;
    try {
        await room.connect(serverUrl, token, configuration);
    } catch (e) {
        room = null;
        return e && e.message ? e.message : "connect-failed";
    }

    state("connected");
    // Failing to open a device must not block the meeting: the user can still listen and chat.
    await room.localParticipant.setMicrophoneEnabled(!!mic).catch(() => { });
    await room.localParticipant.setCameraEnabled(!!camera).catch(() => { });
    report();
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
    try {
        await room.localParticipant.setScreenShareEnabled(on);
    } catch {
        return false;
    }
    return on;
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
}

export function connectionState() {
    return room ? room.state : ConnectionState.Disconnected;
}

function state(value) {
    if (dotnet) dotnet.invokeMethodAsync("OnRoomState", value);
}

function report() {
    if (dotnet && room) dotnet.invokeMethodAsync("OnParticipantCount", room.remoteParticipants.size + 1);
}

function addTile(track, participant, local = false) {
    if (!tiles) return;
    if (track.kind === Track.Kind.Audio) {
        if (local) return; // never play our own microphone back
        const audio = track.attach();
        audio.dataset.sid = track.sid;
        tiles.appendChild(audio);
        return;
    }

    const tile = document.createElement("figure");
    tile.className = "ksc-tile-video";
    tile.dataset.sid = track.sid;
    const video = track.attach();
    video.setAttribute("playsinline", "");
    if (local) video.muted = true;
    const caption = document.createElement("figcaption");
    caption.textContent = (participant && (participant.name || participant.identity)) || "";
    tile.append(video, caption);
    tiles.appendChild(tile);
}

function removeTile(sid) {
    if (!tiles || !sid) return;
    tiles.querySelectorAll(`[data-sid="${CSS.escape(sid)}"]`).forEach(el => el.remove());
}
