// Runs WebAuthn passkey ceremonies in the browser. An ES module shared by the passkey hand-off page served by
// Singulink.Net.Http.Api.Service.Passkeys and by client libraries that run passkeys in browser apps (such as WebAssembly apps).
//
// The ceremony functions take the options the server produced as WebAuthn JSON (PublicKeyCredentialRequestOptionsJSON or
// PublicKeyCredentialCreationOptionsJSON) and return the browser's credential as WebAuthn JSON (the PublicKeyCredential.toJSON() form), the form the
// server reads. The JSON helpers built into newer browsers (PublicKeyCredential.parse*OptionsFromJSON, PublicKeyCredential.toJSON) are used when
// present, with equivalent conversions for browsers that lack them.
//
// Failures reject with an Error whose message is "<DOMException name>: <message>", for example "NotAllowedError: The operation either timed out or
// was not allowed.", so callers can tell a cancelled or timed out prompt (NotAllowedError) or an existing passkey (InvalidStateError) from other
// failures.

function base64UrlToBuffer(value) {
    const base64 = value.replace(/-/g, "+").replace(/_/g, "/") + "===".slice((value.length + 3) % 4);
    const binary = atob(base64);
    const bytes = new Uint8Array(binary.length);

    for (let i = 0; i < binary.length; i++)
        bytes[i] = binary.charCodeAt(i);

    return bytes.buffer;
}

function bufferToBase64Url(buffer) {
    const bytes = new Uint8Array(buffer);
    let binary = "";

    for (let i = 0; i < bytes.length; i++)
        binary += String.fromCharCode(bytes[i]);

    return btoa(binary).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}

function parseCreationOptions(json) {
    const options = JSON.parse(json);

    if (typeof PublicKeyCredential.parseCreationOptionsFromJSON === "function")
        return PublicKeyCredential.parseCreationOptionsFromJSON(options);

    options.challenge = base64UrlToBuffer(options.challenge);
    options.user.id = base64UrlToBuffer(options.user.id);

    for (const descriptor of options.excludeCredentials ?? [])
        descriptor.id = base64UrlToBuffer(descriptor.id);

    return options;
}

function parseRequestOptions(json) {
    const options = JSON.parse(json);

    if (typeof PublicKeyCredential.parseRequestOptionsFromJSON === "function")
        return PublicKeyCredential.parseRequestOptionsFromJSON(options);

    options.challenge = base64UrlToBuffer(options.challenge);

    for (const descriptor of options.allowCredentials ?? [])
        descriptor.id = base64UrlToBuffer(descriptor.id);

    return options;
}

function credentialToJson(credential) {
    if (typeof credential.toJSON === "function") {
        try {
            return credential.toJSON();
        } catch {
            // Some password manager extensions return credentials whose toJSON throws; the conversion below works for those.
        }
    }

    const response = credential.response;
    const json = {
        id: credential.id,
        rawId: bufferToBase64Url(credential.rawId),
        type: credential.type,
        authenticatorAttachment: credential.authenticatorAttachment ?? null,
        clientExtensionResults: credential.getClientExtensionResults(),
        response: { clientDataJSON: bufferToBase64Url(response.clientDataJSON) },
    };

    if (response.attestationObject) {
        json.response.attestationObject = bufferToBase64Url(response.attestationObject);
        json.response.transports = typeof response.getTransports === "function" ? response.getTransports() : [];
    } else {
        json.response.authenticatorData = bufferToBase64Url(response.authenticatorData);
        json.response.signature = bufferToBase64Url(response.signature);
        json.response.userHandle = response.userHandle ? bufferToBase64Url(response.userHandle) : null;
    }

    return json;
}

async function run(ceremony) {
    try {
        if (!isSupported())
            throw new DOMException("This browser does not support passkeys.", "NotSupportedError");

        const credential = await ceremony();

        if (!credential)
            throw new DOMException("No passkey was provided.", "NotAllowedError");

        return JSON.stringify(credentialToJson(credential));
    } catch (e) {
        throw new Error(`${e?.name ?? "Error"}: ${e?.message ?? e}`);
    }
}

/** Gets a value indicating whether the browser supports passkeys. */
export function isSupported() {
    return typeof globalThis.PublicKeyCredential === "function" && !!globalThis.navigator?.credentials;
}

/**
 * Uses a passkey to sign in or confirm the user, with request options (PublicKeyCredentialRequestOptionsJSON) as a JSON string. Returns the credential
 * as a JSON string.
 */
export function getCredential(optionsJson) {
    return run(() => navigator.credentials.get({ publicKey: parseRequestOptions(optionsJson) }));
}

/**
 * Creates a passkey, with creation options (PublicKeyCredentialCreationOptionsJSON) as a JSON string. Returns the credential as a JSON string.
 */
export function createCredential(optionsJson) {
    return run(() => navigator.credentials.create({ publicKey: parseCreationOptions(optionsJson) }));
}

/**
 * Tells the browser that a passkey is not registered with the relying party (WebAuthn's PublicKeyCredential.signalUnknownCredential), so that password
 * managers can stop offering it. The relying party ID defaults to the page's host name, as in ceremonies, and the credential ID is base64url. Returns
 * whether the browser has the call.
 */
export async function signalUnknownCredential(rpId, credentialId) {
    if (typeof globalThis.PublicKeyCredential?.signalUnknownCredential !== "function")
        return false;

    try {
        await PublicKeyCredential.signalUnknownCredential({ rpId: rpId || location.hostname, credentialId });
        return true;
    } catch (e) {
        throw new Error(`${e?.name ?? "Error"}: ${e?.message ?? e}`);
    }
}
