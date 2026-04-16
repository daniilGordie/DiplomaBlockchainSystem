self.addEventListener('message', async (e) => {
    const { prevHash, timestamp, data, pubKey, difficulty } = e.data;
    const target = '0'.repeat(difficulty);
    let nonce = 0;
    let hash = '';

    const encoder = new TextEncoder();

    while (true) {
        const rawData = `${prevHash}${timestamp}${data}${pubKey}${nonce}`;
        const buffer = encoder.encode(rawData);

        const hashBuffer = await crypto.subtle.digest('SHA-256', buffer);

        const hashArray = Array.from(new Uint8Array(hashBuffer));
        hash = hashArray.map(b => b.toString(16).padStart(2, '0')).join('');

        if (hash.startsWith(target)) {
            self.postMessage({ success: true, hash: hash, nonce: nonce });
            break;
        }

        nonce++;

        if (nonce % 5000 === 0) {
            self.postMessage({ success: false, nonce: nonce });
        }
    }
});