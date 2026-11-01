// Script of the passkey hand-off page. It runs one WebAuthn ceremony with the options the page was opened with and returns the credential, or the
// error, to the app's callback address. It never calls an API: the app gets the options from its API and posts the credential back to it.
//
// The server checked the request and wrote it into the data attributes of <main>. The callback address is only there if it is allowed; without it,
// the page shows the server's error and never navigates away.
//
// Browsers that don't hand an address to the app when a script navigates to it, such as some in-app browsers that only do it for a tap, leave the page
// showing. The page then offers a link to the address. Once the page has been hidden after it navigated there, such as behind the app, before the link
// showed or after it was tapped, the app is taken to have the response, and the page only says that the tab can be closed, so that the response is never
// sent again. A page hidden while the link shows untapped, such as by switching apps, keeps the link: it is the only way back to the app.

import { isSupported, getCredential, createCredential } from "./passkeys.js";

// The WebAuthn errors the app is told about by name. Anything else, including errors that are not WebAuthn's (such as options that cannot be read), is
// reported as UnknownError.
const errorNames = ["NotAllowedError", "AbortError", "InvalidStateError", "NotSupportedError", "SecurityError", "ConstraintError", "UnknownError"];

// WebAuthn errors the user can recover from on the page by trying again, such as closing the passkey prompt. All others go straight back to the app,
// since trying again would fail the same way.
const retryableErrorNames = ["NotAllowedError", "AbortError", "UnknownError"];

const main = document.querySelector("main");
const { appName, op, options, redirect, state, errorName, errorMessage } = main.dataset;

// How long the page waits for the browser to return to the app before it offers the link.
const returnLinkDelay = 2000;

const messageText = document.getElementById("message");
const continueButton = document.getElementById("continue");
const cancelButton = document.getElementById("cancel");
const returnLink = document.getElementById("return");

let returnAddress;
let isHandedOff = false;
let isLinkTapped = false;
let returnLinkTimer;

function base64UrlEncode(text) {
    const bytes = new TextEncoder().encode(text);
    let binary = "";

    for (const b of bytes)
        binary += String.fromCharCode(b);

    return btoa(binary).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}

function returnToApp(values) {
    const url = new URL(redirect);

    if (state !== undefined)
        url.searchParams.set("state", state);

    for (const [name, value] of Object.entries(values))
        url.searchParams.set(name, value);

    messageText.textContent = `Returning to ${appName}...`;
    messageText.classList.remove("error");
    continueButton.hidden = true;
    cancelButton.hidden = true;
    returnLink.hidden = true;

    returnAddress = url.href;
    location.replace(returnAddress);

    // A page that navigated away, such as to a loopback address on desktop, is gone before the timer fires.
    clearTimeout(returnLinkTimer);
    returnLinkTimer = setTimeout(showReturnLink, returnLinkDelay);
}

// Shows the link that returns to the app: a tap is a user gesture, which browsers honor for addresses of apps.
function showReturnLink() {
    if (isHandedOff)
        return;

    messageText.textContent = `Tap Return to ${appName} to finish.`;
    returnLink.textContent = `Return to ${appName}`;
    returnLink.href = returnAddress;
    returnLink.hidden = false;
    returnLink.focus();
}

document.addEventListener("visibilitychange", () => {
    if (returnAddress === undefined)
        return;

    if (document.visibilityState === "hidden") {
        if (returnLink.hidden || isLinkTapped) {
            isHandedOff = true;
            clearTimeout(returnLinkTimer);
        }
    } else if (isHandedOff) {
        messageText.textContent = "You can close this tab.";
        returnLink.hidden = true;
    }
});

function returnError(name, message) {
    returnToApp({ error: name, message });
}

// passkeys.js reports failures as "<error name>: <message>".
function parseError(e) {
    const text = e?.message ?? String(e);
    const match = /^(\w+): ([\s\S]*)$/.exec(text);
    const known = match !== null && errorNames.includes(match[1]);

    return {
        name: known ? match[1] : "UnknownError",
        message: match ? match[2] : text,
        retryable: known && retryableErrorNames.includes(match[1]),
    };
}

async function runCeremony() {
    continueButton.disabled = true;

    try {
        const credential = op === "create" ? await createCredential(options) : await getCredential(options);
        returnToApp({ credential: base64UrlEncode(credential) });
    } catch (e) {
        const { name, message, retryable } = parseError(e);

        if (retryable) {
            messageText.textContent = name === "UnknownError" ? message : "The passkey prompt was closed or timed out.";
            messageText.classList.add("error");
            continueButton.textContent = "Try again";
            continueButton.disabled = false;
        } else {
            returnError(name, message);
        }
    }
}

if (redirect !== undefined) {
    returnLink.addEventListener("click", () => isLinkTapped = true);

    if (errorName !== undefined)
        returnError(errorName, errorMessage ?? "");
    else if (!isSupported())
        returnError("NotSupportedError", "This browser does not support passkeys.");
    else {
        continueButton.addEventListener("click", runCeremony);
        cancelButton.addEventListener("click", () => returnError("AbortError", "The user cancelled."));
    }
}
