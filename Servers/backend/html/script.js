function connectWebSocket() {
    const ws = new WebSocket(wsUrl);

    ws.onopen = () => {
        ws.send(JSON.stringify({ action: 'web_listen' }));
    };

    const elements = {
        users: document.getElementById('stats-users'),
        clips_synced: document.getElementById('stats-sent'),
        clips_taken: document.getElementById('stats-received')
    };

    const currentVals = {
        users: parseInt(elements.users.innerText.replace(/,/g, '')) || 0,
        clips_synced: parseInt(elements.clips_synced.innerText.replace(/,/g, '')) || 0,
        clips_taken: parseInt(elements.clips_taken.innerText.replace(/,/g, '')) || 0
    };

    const intervals = { users: null, clips: null };

    ws.onmessage = (event) => {
        const data = JSON.parse(event.data);
        if (data.action === 'stats_update') {
            updateUsers(data.users);
            updateClips(data.clips_synced, data.clips_taken);
        }
    };

    ws.onclose = () => {
        setTimeout(connectWebSocket, 5000);
    };

    function animateElement(el) {
        el.classList.remove('swipe-animate');
        void el.offsetWidth;
        el.classList.add('swipe-animate');
    }

    function updateUsers(targetVal) {
        if (intervals.users) clearInterval(intervals.users);

        const diff = targetVal - currentVals.users;
        if (diff <= 0) {
            currentVals.users = targetVal;
            elements.users.innerText = currentVals.users;
            return;
        }

        const intervalMs = Math.floor(20000 / diff);
        let added = 0;

        intervals.users = setInterval(() => {
            currentVals.users++;
            elements.users.innerText = currentVals.users;
            animateElement(elements.users);

            added++;
            if (added >= diff) {
                clearInterval(intervals.users);
            }
        }, intervalMs);
    }

    function updateClips(targetSynced, targetTaken) {
        if (intervals.clips) clearInterval(intervals.clips);

        const diffSynced = targetSynced - currentVals.clips_synced;
        const diffTaken = targetTaken - currentVals.clips_taken;

        if (diffSynced <= 0) {
            currentVals.clips_synced = targetSynced;
            elements.clips_synced.innerText = currentVals.clips_synced;
            currentVals.clips_taken = targetTaken;
            elements.clips_taken.innerText = currentVals.clips_taken;
            return;
        }

        const intervalMs = Math.floor(20000 / diffSynced);
        const takenPerTick = diffTaken / diffSynced;
        
        let addedSynced = 0;
        let exactTaken = currentVals.clips_taken;

        intervals.clips = setInterval(() => {
            currentVals.clips_synced++;
            elements.clips_synced.innerText = currentVals.clips_synced;
            animateElement(elements.clips_synced);

            exactTaken += takenPerTick;
            currentVals.clips_taken = Math.round(exactTaken);
            elements.clips_taken.innerText = currentVals.clips_taken;
            animateElement(elements.clips_taken);

            addedSynced++;
            if (addedSynced >= diffSynced) {
                clearInterval(intervals.clips);
                currentVals.clips_taken = targetTaken;
                elements.clips_taken.innerText = currentVals.clips_taken;
            }
        }, intervalMs);
    }
}

connectWebSocket();