window.downloadFileFromStream = async (fileName, contentStreamReference) => {
    const arrayBuffer = await contentStreamReference.arrayBuffer();
    const blob = new Blob([arrayBuffer]);
    const url = URL.createObjectURL(blob);
    const anchorElement = document.createElement('a');
    anchorElement.href = url;
    anchorElement.download = fileName ?? '';
    anchorElement.click();
    anchorElement.remove();
    URL.revokeObjectURL(url);
};

window.nexusPasskey = {
    _b64ToBytes: (b64) => Uint8Array.from(atob(b64), c => c.charCodeAt(0)),
    _bytesToB64: (bytes) => btoa(String.fromCharCode(...new Uint8Array(bytes))),
    _b64UrlToBytes: (b64url) => {
        const b64 = b64url.replace(/-/g, '+').replace(/_/g, '/')
            + '='.repeat((4 - b64url.length % 4) % 4);
        return Uint8Array.from(atob(b64), c => c.charCodeAt(0));
    },
    _bytesToB64Url: (bytes) => btoa(String.fromCharCode(...new Uint8Array(bytes)))
        .replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/g, ''),
    _random: (n) => {
        const a = new Uint8Array(n);
        crypto.getRandomValues(a);
        return a;
    },
    _toUint8: (value) => {
        if (!value) return new Uint8Array();
        if (value instanceof Uint8Array) return value;
        if (value instanceof ArrayBuffer) return new Uint8Array(value);
        if (ArrayBuffer.isView(value)) return new Uint8Array(value.buffer, value.byteOffset, value.byteLength);
        return new Uint8Array();
    },
    registerAndWrapKey: async (userName, walletKeyBase64) => {
        if (!window.PublicKeyCredential || !navigator.credentials) {
            throw new Error('WebAuthn is not supported by this browser.');
        }
        if (!window.isSecureContext) {
            throw new Error('Passkey requires secure context (https or localhost).');
        }
        if (window.PublicKeyCredential.isUserVerifyingPlatformAuthenticatorAvailable) {
            const hasPlatform = await window.PublicKeyCredential.isUserVerifyingPlatformAuthenticatorAvailable();
            if (!hasPlatform) {
                throw new Error('No platform authenticator available (Windows Hello / Touch ID / Google passkey).');
            }
        }

        const challenge = window.nexusPasskey._random(32);
        const userBytes = new TextEncoder().encode(userName || "nexus-user");
        const userId = userBytes.length > 64 ? userBytes.slice(0, 64) : userBytes;

        const credential = await navigator.credentials.create({
            publicKey: {
                challenge,
                rp: { name: "Nexus Wallet" },
                user: {
                    id: userId,
                    name: userName || "nexus-user",
                    displayName: userName || "Nexus User"
                },
                pubKeyCredParams: [
                    { type: "public-key", alg: -7 },
                    { type: "public-key", alg: -257 }
                ],
                authenticatorSelection: {
                    residentKey: "required",
                    authenticatorAttachment: "platform",
                    userVerification: "preferred"
                },
                extensions: {
                    largeBlob: {
                        support: "preferred"
                    }
                },
                timeout: 120000,
                attestation: "none"
            }
        });

        const rawIdBytes = new Uint8Array(credential.rawId);
        const credentialId = window.nexusPasskey._bytesToB64Url(rawIdBytes);
        const extCreate = credential.getClientExtensionResults?.() || {};
        if (!extCreate.largeBlob || extCreate.largeBlob.supported !== true) {
            throw new Error('This authenticator does not support passkey large-blob storage.');
        }

        const writeChallenge = window.nexusPasskey._random(32);
        const walletKey = window.nexusPasskey._b64ToBytes(walletKeyBase64);

        const writeAssertion = await navigator.credentials.get({
            publicKey: {
                challenge: writeChallenge,
                allowCredentials: [{ type: "public-key", id: rawIdBytes }],
                userVerification: "required",
                timeout: 120000,
                extensions: {
                    largeBlob: {
                        write: walletKey
                    }
                }
            }
        });

        const extWrite = writeAssertion.getClientExtensionResults?.() || {};
        if (!extWrite.largeBlob || extWrite.largeBlob.written !== true) {
            throw new Error('Passkey credential could not store wallet key blob.');
        }

        return {
            credentialId,
            wrappedKey: "webauthn-largeblob-v1"
        };
    },
    verifyAndUnwrapKey: async (credentialId, wrappedKeyBase64) => {
        if (!window.PublicKeyCredential || !navigator.credentials) {
            return { success: false, unwrappedKeyBase64: "" };
        }
        if (!window.isSecureContext) {
            throw new Error('Passkey requires secure context (https or localhost).');
        }

        const challenge = window.nexusPasskey._random(32);
        const credIdBytes = window.nexusPasskey._b64UrlToBytes(credentialId);

        const assertion = await navigator.credentials.get({
            publicKey: {
                challenge,
                allowCredentials: [{ type: "public-key", id: credIdBytes }],
                userVerification: "required",
                timeout: 120000,
                extensions: {
                    largeBlob: {
                        read: true
                    }
                }
            }
        });

        if (!assertion) return { success: false, unwrappedKeyBase64: "" };

        const clientData = JSON.parse(new TextDecoder().decode(assertion.response.clientDataJSON));
        const challengeB64Url = window.nexusPasskey._bytesToB64Url(challenge);
        if (clientData.challenge !== challengeB64Url) {
            return { success: false, unwrappedKeyBase64: "" };
        }

        const extRead = assertion.getClientExtensionResults?.() || {};
        const blob = extRead.largeBlob?.blob;
        const walletKeyBytes = window.nexusPasskey._toUint8(blob);
        if (!walletKeyBytes || walletKeyBytes.length === 0) {
            return { success: false, unwrappedKeyBase64: "" };
        }

        return {
            success: true,
            unwrappedKeyBase64: window.nexusPasskey._bytesToB64(walletKeyBytes)
        };
    }
};
